function recorder() {
  return {
    token: localStorage.getItem('va_token') || '',
    me: null,
    view: 'pick',           // 'pick' | 'record' (login wird über !token gesteuert)
    loginForm: { username: '', password: '' },
    loginError: '',
    err: '',

    keys: [],
    newKeyName: '',
    selectedKeyId: null,
    profile: null,
    uiLang: 'de',
    enabled: [],            // zusätzliche Sprachen aus dem Profil
    language: '',           // aktuelle Erkennungssprache ('' = Auto)
    temperature: 0,
    promptText: '',

    recording: false,
    busy: false,
    elapsed: 0,
    status: 'Bereit.',
    _statusCls: '',
    transcript: '',
    lastMs: 0,
    toast: '',

    _mr: null, _stream: null, _chunks: [], _timer: null,

    // ---------- Lifecycle ----------
    async init() {
      this.registerSW();
      if (!this.token) return;
      try { this.me = await this.apiJson('GET', '/api/auth/me'); }
      catch { this.logout(); return; }
      await this.afterAuth();
    },

    async afterAuth() {
      await this.loadKeys();
      const saved = localStorage.getItem('va_key_id');
      if (saved && this.keys.some(k => String(k.id) === String(saved))) {
        this.selectedKeyId = parseInt(saved);
        await this.loadProfile();
        this.view = 'record';
      } else {
        this.view = 'pick';
      }
    },

    registerSW() {
      if ('serviceWorker' in navigator) {
        navigator.serviceWorker.register('/sw.js').catch(() => {});
      }
    },

    // ---------- Auth ----------
    async login() {
      this.loginError = '';
      try {
        const body = new URLSearchParams();
        body.set('username', this.loginForm.username);
        body.set('password', this.loginForm.password);
        const r = await fetch('/api/auth/login', { method: 'POST', body });
        if (!r.ok) throw new Error('Login fehlgeschlagen');
        const data = await r.json();
        this.token = data.access_token;
        localStorage.setItem('va_token', this.token);
        this.me = await this.apiJson('GET', '/api/auth/me');
        await this.afterAuth();
      } catch (e) { this.loginError = e.message; }
    },

    logout() {
      this.token = ''; this.me = null; this.profile = null;
      this.view = 'pick';
      localStorage.removeItem('va_token');
    },

    // ---------- API helper (JWT) ----------
    async apiJson(method, path, body) {
      const opts = { method, headers: { 'Authorization': 'Bearer ' + this.token } };
      if (body !== undefined) {
        opts.headers['Content-Type'] = 'application/json';
        opts.body = JSON.stringify(body);
      }
      const r = await fetch(path, opts);
      if (r.status === 401) { this.logout(); throw new Error('unauthorized'); }
      if (!r.ok) {
        const e = await r.json().catch(() => ({ detail: r.statusText }));
        throw new Error(e.detail || 'request failed');
      }
      if (r.status === 204) return null;
      return r.json();
    },

    // ---------- Keys ----------
    async loadKeys() { this.keys = await this.apiJson('GET', '/api/api-keys'); },

    async createKey() {
      this.err = '';
      try {
        const k = await this.apiJson('POST', '/api/api-keys', { name: this.newKeyName });
        this.newKeyName = '';
        await this.loadKeys();
        await this.selectKey(k.id);
      } catch (e) { this.err = e.message; }
    },

    async selectKey(id) {
      this.selectedKeyId = id;
      localStorage.setItem('va_key_id', String(id));
      await this.loadProfile();
      this.view = 'record';
      this.status = 'Bereit.'; this._statusCls = '';
    },

    changeKey() { this.view = 'pick'; this.loadKeys(); },

    // ---------- Profile ----------
    async loadProfile() {
      this.profile = await this.apiJson('GET', '/api/web/keys/' + this.selectedKeyId + '/profile');
      this.afterProfile();
    },

    afterProfile() {
      const cs = this.profile.client_settings || {};
      this.uiLang = cs.uiLanguage || 'de';
      this.language = cs.language || '';
      this.enabled = Array.isArray(cs.enabledLanguages) ? cs.enabledLanguages : [];
      this.temperature = this.profile.temperature ?? 0;
      this.syncPromptText();
    },

    get languageOptions() {
      const out = ['', 'de', 'en'];
      for (const c of this.enabled) {
        const lc = (c || '').toLowerCase();
        if (lc && lc !== 'de' && lc !== 'en' && !out.includes(lc)) out.push(lc);
      }
      return out;
    },

    langLabel(code) { return window.vaLangName(code, this.uiLang); },

    async setLanguage(code) {
      this.language = code;
      this.syncPromptText();
      try { this.profile = await this.apiJson('PUT', '/api/web/keys/' + this.selectedKeyId + '/language', { language: code }); }
      catch (e) { /* nicht kritisch fürs Aufnehmen */ }
    },

    syncPromptText() {
      const p = this.profile || {};
      if (this.language === 'de') this.promptText = p.prompt_de || '';
      else if (this.language === 'en') this.promptText = p.prompt_en || '';
      else if (this.language) this.promptText = (p.language_prompts && p.language_prompts[this.language]) || '';
      else this.promptText = '';
    },

    async savePrompt() {
      const body = {};
      if (this.language === 'de') body.prompt_de = this.promptText;
      else if (this.language === 'en') body.prompt_en = this.promptText;
      else if (this.language) {
        const m = Object.assign({}, this.profile.language_prompts || {});
        m[this.language] = this.promptText;
        body.language_prompts = m;
      } else return;
      try { this.profile = await this.apiJson('PUT', '/api/web/keys/' + this.selectedKeyId + '/profile', body); this.afterProfile(); this.showToast('Gespeichert'); }
      catch (e) { this.showToast('Fehler: ' + e.message); }
    },

    async saveTemperature() {
      try { this.profile = await this.apiJson('PUT', '/api/web/keys/' + this.selectedKeyId + '/profile', { temperature: this.temperature }); }
      catch (e) { /* ignore */ }
    },

    // ---------- Recording ----------
    toggleRecord() { this.recording ? this.stopRec() : this.startRec(); },

    pickMime() {
      const c = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/mp4'];
      for (const m of c) { if (window.MediaRecorder && MediaRecorder.isTypeSupported(m)) return m; }
      return '';
    },

    async startRec() {
      if (this.busy) return;
      try { this._stream = await navigator.mediaDevices.getUserMedia({ audio: true }); }
      catch (e) { this.setStatus('Mikrofon-Zugriff verweigert.', 'err'); return; }
      const mime = this.pickMime();
      this._chunks = [];
      this._mr = new MediaRecorder(this._stream, mime ? { mimeType: mime } : undefined);
      this._mr.ondataavailable = e => { if (e.data && e.data.size) this._chunks.push(e.data); };
      this._mr.onstop = () => this.onStopped();
      this._mr.start();
      this.recording = true;
      this.elapsed = 0;
      this.setStatus('Aufnahme läuft …', 'rec');
      this._timer = setInterval(() => { this.elapsed++; }, 1000);
    },

    stopRec() {
      if (this._mr && this._mr.state !== 'inactive') this._mr.stop();
    },

    onStopped() {
      clearInterval(this._timer);
      this.recording = false;
      if (this._stream) { this._stream.getTracks().forEach(t => t.stop()); this._stream = null; }
      const type = (this._mr && this._mr.mimeType) || 'audio/webm';
      const blob = new Blob(this._chunks, { type });
      this._chunks = [];
      this.sendAudio(blob);
    },

    async sendAudio(blob) {
      if (blob.size < 1024) { this.setStatus('Zu kurz, verworfen.', 'err'); return; }
      this.busy = true;
      this.setStatus('Sende Audio …', 'busy');
      const ext = blob.type.includes('ogg') ? 'ogg' : (blob.type.includes('mp4') ? 'mp4' : 'webm');
      const fd = new FormData();
      fd.append('file', blob, 'rec.' + ext);
      fd.append('language', this.language);
      try {
        const r = await fetch('/api/web/keys/' + this.selectedKeyId + '/transcribe', {
          method: 'POST',
          headers: { 'Authorization': 'Bearer ' + this.token },
          body: fd,
        });
        if (r.status === 401) { this.logout(); return; }
        if (!r.ok) { const e = await r.json().catch(() => ({ detail: r.statusText })); throw new Error(e.detail || 'Fehler'); }
        const data = await r.json();
        this.transcript = data.text || '';
        this.lastMs = data.processing_ms || 0;
        this.setStatus(data.text ? 'Fertig.' : 'Leeres Transkript.', data.text ? 'done' : 'err');
      } catch (e) {
        this.setStatus('Fehler: ' + e.message, 'err');
      } finally { this.busy = false; }
    },

    // ---------- UI helpers ----------
    setStatus(text, cls) { this.status = text; this._statusCls = cls || ''; },
    get statusClass() { return this._statusCls; },

    get wordCount() {
      const t = (this.transcript || '').trim();
      return t ? t.split(/\s+/).length : 0;
    },

    fmtTime(s) {
      const m = Math.floor(s / 60), x = s % 60;
      return String(m).padStart(2, '0') + ':' + String(x).padStart(2, '0');
    },

    async copy() {
      if (!this.transcript) return;
      try { await navigator.clipboard.writeText(this.transcript); }
      catch {
        const ta = document.createElement('textarea');
        ta.value = this.transcript; document.body.appendChild(ta); ta.select();
        try { document.execCommand('copy'); } catch {}
        document.body.removeChild(ta);
      }
      this.showToast('Kopiert');
    },

    showToast(msg) { this.toast = msg; clearTimeout(this._toastT); this._toastT = setTimeout(() => { this.toast = ''; }, 1800); },
  };
}
