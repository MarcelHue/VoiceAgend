# VoiceAgend

Selbstgehosteter Speech-to-Text-Dienst mit nativem Windows-Client.

```
┌──────────────────┐    WebSocket      ┌─────────────────┐    HTTP    ┌────────────┐
│  WinUI 3 Client  │  Opus/Ogg ──────► │  VoiceAgend     │  ────────► │  Speaches  │
│  (Hotkey/Tray)   │                   │  (FastAPI)      │            │  (Whisper) │
│                  │  ◄──── Text       │  Auth, UI, DB   │  ◄────     │            │
└──────────────────┘                   └─────────────────┘            └────────────┘
```

## Komponenten

- **`client/`** — Windows-Client (C# / .NET 9 / WinUI 3) mit Tray-Icon, globalem Hotkey, HUD-Overlay, Auto-Update via [Velopack](https://github.com/velopack/velopack)
- **`server/`** — FastAPI-Gateway: Authentifizierung, Web-Dashboard (Login, Benutzer- und API-Key-Verwaltung, Statistik), WebSocket-Endpoint, Profil-Verwaltung pro API-Key
- **`whisper/`** — [Speaches](https://github.com/speaches-ai/speaches) als eigenständiger Whisper-Container, OpenAI-API-kompatibel — auch von Home Assistant, n8n usw. nutzbar

## Schnellstart für Endnutzer

1. Auf der **Releases-Seite** des Repos den neuesten Release herunterladen
2. `VoiceAgend-win-Setup.exe` ausführen — App installiert sich nach `%LocalAppData%\VoiceAgend`
3. App startet automatisch ins Tray; Settings-Fenster öffnet sich beim ersten Start
4. **Server-URL** und **API-Key** vom Server-Admin eintragen
5. Mikrofon und Hotkey wählen (Default: `Strg+Shift+R`)
6. Hotkey drücken → Aufnahme. Nochmal drücken → Transkript landet in Zwischenablage / aktivem Fenster / als Notification

Auto-Updates laufen im Hintergrund: bei neuer Release-Version erscheint im Settings-Tab ein „Update installieren"-Button.

---

## Server-Setup (für Self-Hoster)

### Voraussetzungen

- TrueNAS SCALE 24.10+ (oder beliebiges Linux mit Docker)
- 16+ GB RAM (Whisper-`medium` braucht ~3 GB, `large-v3` ~6 GB)
- Reverse Proxy mit WebSocket-Support (z. B. nginx-proxymanager) und HTTPS-Zertifikat
- Optional: NVIDIA-GPU (Turing+ / RTX 20xx aufwärts) für deutlich schnellere Transkription. Ältere Karten wie Pascal werden vom in TrueNAS 24.10+ ausgelieferten Open-Source-Treiber **nicht** unterstützt — CPU-Modus läuft mit `medium` auf einem 6-Kerner ungefähr in Echtzeit, was für Diktat ausreicht.

### 1. Datasets anlegen (TrueNAS)

In der Apps-Konvention unter dem Pool deiner Wahl, z. B.:

```
<pool>/Applications/voiceagend/data        Owner: apps:apps   (UID/GID 568)
<pool>/Applications/speaches/cache         Owner: 1000:1000   (Speaches läuft als ubuntu)
<pool>/Applications/speaches/config        Owner: 1000:1000
```

Das `cache`-Verzeichnis braucht ein Sub-`hub`:
```bash
sudo mkdir -p /mnt/<pool>/Applications/speaches/cache/hub
sudo chown -R 1000:1000 /mnt/<pool>/Applications/speaches/cache
```

### 2. Speaches als Custom App

**Apps → Discover → Custom App** mit Inhalt aus [`whisper/docker-compose.truenas.yml`](whisper/docker-compose.truenas.yml). Pool-Namen anpassen.

Modelle werden nicht automatisch geladen, einmalig anstoßen:

```bash
curl -X POST http://<truenas-ip>:8001/v1/models/Systran/faster-whisper-medium
```

### 3. VoiceAgend-Server als Custom App

Image bauen und nach GHCR pushen — automatisch via [`.github/workflows/build-server.yml`](.github/workflows/build-server.yml) bei jedem Push auf `main`. Image-Adresse: `ghcr.io/<dein-user>/voiceagend-server:latest-cpu`.

Custom-App-YAML aus [`server/docker-compose.truenas.yml`](server/docker-compose.truenas.yml) anpassen:
- Image-Pfad einsetzen
- `VOICEAGEND_WHISPER_URL` auf die TrueNAS-IP + Port 8001 zeigen
- `VOICEAGEND_JWT_SECRET` und `VOICEAGEND_ADMIN_PASSWORD` setzen (keine `$`-Zeichen, sonst escape als `$$`)

### 4. Reverse Proxy (nginx-proxymanager)

- Domain → `<truenas-ip>:8000`
- HTTPS aktivieren
- Custom Nginx Configuration:
  ```nginx
  proxy_read_timeout 3600s;
  proxy_send_timeout 3600s;
  proxy_buffering off;
  ```
  WebSocket-Header **nicht** zusätzlich setzen — NPM+ macht das selbst.

### 5. Erstkonfiguration

- Domain im Browser öffnen → mit `Admin` und dem gesetzten Passwort einloggen
- **API-Keys** anlegen (einer pro Gerät empfohlen)
- Den `va_…`-Key kopieren — wird nur einmal angezeigt
- Im Windows-Client Server-URL und API-Key eintragen

---

## Client-Build (für Entwickler)

Siehe [`client/README.md`](client/README.md). Kurz:

```powershell
cd client
dotnet build VoiceAgend.sln
```

Voraussetzungen: .NET 9 SDK, Windows App SDK 1.6 Runtime (oder `WindowsAppSDKSelfContained=true` für portable Builds).

## API

| Endpoint | Auth | Zweck |
|---|---|---|
| `GET  /` | – | Web-Dashboard |
| `POST /api/auth/login` | – | Login, liefert JWT |
| `GET/POST/PATCH/DELETE /api/users` | JWT (admin) | Benutzer-CRUD |
| `GET/POST/DELETE /api/api-keys` | JWT | API-Keys des Users |
| `GET /api/stats/me` · `/api/stats/global` | JWT | Statistik |
| `GET /api/v1/models` | API-Key | Verfügbare Whisper-Modelle |
| `GET/PUT /api/v1/profile` | API-Key | Pro-Key-Profil (Modell, Prompt, Temperature) |
| `WS  /ws/transcribe` | API-Key (im JSON-Header) | Audio-Übertragung + Transkript |

## WebSocket-Protokoll `/ws/transcribe`

1. Client sendet Text-Frame: `{"api_key":"va_…","language":"de"}`
2. Beliebig viele Binary-Frames mit Opus/Ogg-Audio-Chunks
3. Text-Frame `{"end": true}`
4. Server: `{"status":"processing"}`
5. Server: `{"text":"…","language":"de","processing_ms":980}` und schließt

## Code Signing

Aktuell ist der Installer **nicht signiert**. Beim Ausführen zeigt Windows SmartScreen
eine Warnung — über **„Weitere Informationen" → „Trotzdem ausführen"** lässt sich der
Installer starten.

Eine Bewerbung bei der [SignPath Foundation](https://signpath.org/apply) für kostenloses
OSS-Code-Signing läuft. Sobald genehmigt, werden zukünftige Releases signiert ausgeliefert
und SmartScreen-Warnungen entfallen.

## Lizenz

Pro Komponente unterschiedlich — siehe [LICENSE](LICENSE) für die Übersicht:

- **`client/`** — [GPL-3.0-or-later](client/LICENSE)
- **`server/`** — [AGPL-3.0-or-later](server/LICENSE)
- **`whisper/`** — nur Compose-Konfiguration; Speaches ist eigenständig lizenziert

Forks und Modifikationen müssen unter denselben Lizenzen weitergegeben werden. Bei Fragen
oder kommerziellen Sonder-Lizenzen → Issue im Repo eröffnen.
