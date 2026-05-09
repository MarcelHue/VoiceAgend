function dashboard() {
  return {
    token: localStorage.getItem('va_token') || '',
    me: null,
    tab: 'stats',
    loginForm: { username: '', password: '' },
    loginError: '',
    stats: null,
    globalStats: false,
    keys: [],
    newKeyName: '',
    newKeyValue: '',
    users: [],
    newUser: { username: '', password: '', is_admin: false },

    async init() {
      if (this.token) {
        try {
          this.me = await this.api('GET', '/api/auth/me');
          await this.loadStats();
        } catch {
          this.logout();
        }
      }
    },

    async api(method, path, body) {
      const opts = { method, headers: {} };
      if (this.token) opts.headers['Authorization'] = 'Bearer ' + this.token;
      if (body !== undefined) {
        opts.headers['Content-Type'] = 'application/json';
        opts.body = JSON.stringify(body);
      }
      const r = await fetch(path, opts);
      if (r.status === 401) { this.logout(); throw new Error('unauthorized'); }
      if (!r.ok) {
        const err = await r.json().catch(() => ({ detail: r.statusText }));
        throw new Error(err.detail || 'request failed');
      }
      if (r.status === 204) return null;
      return r.json();
    },

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
        this.me = await this.api('GET', '/api/auth/me');
        await this.loadStats();
      } catch (e) {
        this.loginError = e.message;
      }
    },

    logout() {
      this.token = '';
      this.me = null;
      localStorage.removeItem('va_token');
    },

    async loadStats() {
      const path = (this.globalStats && this.me?.is_admin) ? '/api/stats/global' : '/api/stats/me';
      this.stats = await this.api('GET', path);
    },

    async loadKeys() {
      this.keys = await this.api('GET', '/api/api-keys');
    },

    async createKey() {
      const r = await this.api('POST', '/api/api-keys', { name: this.newKeyName });
      this.newKeyValue = r.key;
      this.newKeyName = '';
      await this.loadKeys();
    },

    async deleteKey(id) {
      if (!confirm('Schlüssel wirklich löschen?')) return;
      await this.api('DELETE', '/api/api-keys/' + id);
      await this.loadKeys();
    },

    async loadUsers() {
      this.users = await this.api('GET', '/api/users');
    },

    async createUser() {
      await this.api('POST', '/api/users', this.newUser);
      this.newUser = { username: '', password: '', is_admin: false };
      await this.loadUsers();
    },

    async patchUser(id, payload) {
      await this.api('PATCH', '/api/users/' + id, payload);
      await this.loadUsers();
    },

    async resetPassword(u) {
      const pw = prompt('Neues Passwort für ' + u.username + ' (min. 8 Zeichen):');
      if (!pw) return;
      await this.patchUser(u.id, { password: pw });
    },

    async deleteUser(id) {
      if (!confirm('Benutzer wirklich löschen?')) return;
      await this.api('DELETE', '/api/users/' + id);
      await this.loadUsers();
    },

    formatDate(s) {
      if (!s) return '–';
      return new Date(s).toLocaleString('de-DE');
    },
  };
}
