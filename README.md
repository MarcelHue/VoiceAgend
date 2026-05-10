# VoiceAgend

Self-hosted speech-to-text service with a native Windows client.

```
┌──────────────────┐    WebSocket      ┌─────────────────┐    HTTP    ┌────────────┐
│  WinUI 3 Client  │  Opus/Ogg ──────► │  VoiceAgend     │  ────────► │  Speaches  │
│  (Hotkey/Tray)   │                   │  (FastAPI)      │            │  (Whisper) │
│                  │  ◄──── Text       │  Auth, UI, DB   │  ◄────     │            │
└──────────────────┘                   └─────────────────┘            └────────────┘
```

## Components

- **`client/`** — Windows client (C# / .NET 9 / WinUI 3) with tray icon, global hotkey, on-screen HUD, auto-update via [Velopack](https://github.com/velopack/velopack)
- **`server/`** — FastAPI gateway: authentication, web dashboard (login, user/API-key management, statistics), WebSocket endpoint, per-key transcription profiles
- **`whisper/`** — [Speaches](https://github.com/speaches-ai/speaches) as a stand-alone Whisper container, OpenAI-API compatible — also usable from Home Assistant, n8n, etc.

## Quick start (end users)

1. Download the latest installer from the **Releases** page of this repo
2. Run `VoiceAgend-win-Setup.exe` — the app installs to `%LocalAppData%\VoiceAgend`
3. The tray icon appears; the settings window opens on first launch
4. Enter the **Server URL** and **API key** that your server admin provides
5. Pick a microphone and a hotkey (default: `Ctrl+Shift+R`)
6. Press the hotkey → recording. Press again → the transcript lands in your clipboard / active window / a notification

Auto-updates run quietly in the background; when a new release lands, an "Install update" button appears in the settings tab.

---

## Server setup (self-hosters)

### Prerequisites

- TrueNAS SCALE 24.10+ (or any Linux host with Docker)
- 16+ GB RAM (Whisper `medium` needs ~3 GB, `large-v3` ~6 GB)
- A reverse proxy with WebSocket support (e.g. nginx-proxymanager) and an HTTPS certificate
- Optional: NVIDIA GPU (Turing+ / RTX 20xx and later) for much faster transcription. Older cards like Pascal are **not** supported by the open-source NVIDIA driver shipped with TrueNAS 24.10+. CPU mode runs `medium` near real-time on a modern 6-core, which is fine for dictation.

### 1. Create datasets (TrueNAS)

Conventional layout under your chosen pool:

```
<pool>/Applications/voiceagend/data        Owner: apps:apps   (UID/GID 568)
<pool>/Applications/speaches/cache         Owner: 1000:1000   (Speaches runs as ubuntu)
<pool>/Applications/speaches/config        Owner: 1000:1000
```

The `cache` directory needs a `hub` sub-folder:
```bash
sudo mkdir -p /mnt/<pool>/Applications/speaches/cache/hub
sudo chown -R 1000:1000 /mnt/<pool>/Applications/speaches/cache
```

### 2. Speaches as a Custom App

**Apps → Discover → Custom App**, paste [`whisper/docker-compose.truenas.yml`](whisper/docker-compose.truenas.yml). Adjust the pool name.

Models are not auto-loaded — trigger one once:
```bash
curl -X POST http://<truenas-ip>:8001/v1/models/Systran/faster-whisper-medium
```

### 3. VoiceAgend server as a Custom App

The image is built and pushed to GHCR automatically on every `main` push by [`.github/workflows/build-server.yml`](.github/workflows/build-server.yml). Image address: `ghcr.io/<your-user>/voiceagend-server:latest-cpu`.

Adjust the YAML in [`server/docker-compose.truenas.yml`](server/docker-compose.truenas.yml):
- Set the image path
- Point `VOICEAGEND_WHISPER_URL` at the TrueNAS IP + port 8001
- Set `VOICEAGEND_JWT_SECRET` and `VOICEAGEND_ADMIN_PASSWORD` (no `$` characters, or escape them as `$$`)

### 4. Reverse proxy (nginx-proxymanager)

- Domain → `<truenas-ip>:8000`
- Enable HTTPS
- Custom Nginx Configuration:
  ```nginx
  proxy_read_timeout 3600s;
  proxy_send_timeout 3600s;
  proxy_buffering off;
  ```
  Do **not** add WebSocket headers manually — NPM+ injects them already.

### 5. First-time configuration

- Open the domain in a browser → log in with `Admin` and the password you set
- Create **API keys** (one per device is recommended)
- Copy the `va_…` key — it is only shown once
- Enter the server URL and the API key in the Windows client

---

## Client build (developers)

See [`client/README.md`](client/README.md). In short:

```powershell
cd client
dotnet build VoiceAgend.sln
```

Requirements: .NET 9 SDK, Windows App SDK 1.6 runtime (or `WindowsAppSDKSelfContained=true` for portable builds).

## API

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET  /` | – | Web dashboard |
| `POST /api/auth/login` | – | Login, returns JWT |
| `GET/POST/PATCH/DELETE /api/users` | JWT (admin) | User CRUD |
| `GET/POST/DELETE /api/api-keys` | JWT | API keys for the logged-in user |
| `GET /api/stats/me` · `/api/stats/global` | JWT | Statistics |
| `GET /api/v1/models` | API key | Available Whisper models |
| `POST /api/v1/models/{id}` | API key | Trigger model download on Speaches |
| `GET/PUT /api/v1/profile` | API key | Per-key profile (model, prompt, temperature) |
| `WS  /ws/transcribe` | API key (in JSON header) | Audio upload + transcript |

## WebSocket protocol `/ws/transcribe`

1. Client sends a text frame: `{"api_key":"va_…","language":"de"}`
2. Any number of binary frames carrying Opus/Ogg audio chunks
3. Text frame: `{"end": true}`
4. Server: `{"status":"processing"}`
5. Server: `{"text":"…","language":"de","processing_ms":980}` and closes

## Code Signing

The installer is currently **unsigned**. Windows SmartScreen will warn —
click **"More info" → "Run anyway"** to install.

An application has been submitted to the [SignPath Foundation](https://signpath.org/apply)
for free OSS code signing. Once approved, future releases will ship signed and SmartScreen
warnings will go away.

## Licensing

License differs per component — see [LICENSE](LICENSE) for the overview:

- **`client/`** — [GPL-3.0-or-later](client/LICENSE)
- **`server/`** — [AGPL-3.0-or-later](server/LICENSE)
- **`whisper/`** — Compose configuration only; Speaches is licensed by its upstream project

Forks and modifications must be redistributed under the same licenses. For commercial
or alternative licensing, please open an issue.

---
---

# VoiceAgend (Deutsch)

Selbstgehosteter Speech-to-Text-Dienst mit nativem Windows-Client.

## Komponenten

- **`client/`** — Windows-Client (C# / .NET 9 / WinUI 3) mit Tray-Icon, globalem Hotkey, HUD-Overlay, Auto-Update via [Velopack](https://github.com/velopack/velopack)
- **`server/`** — FastAPI-Gateway: Authentifizierung, Web-Dashboard (Login, Benutzer- und API-Key-Verwaltung, Statistik), WebSocket-Endpoint, Profil-Verwaltung pro API-Key
- **`whisper/`** — [Speaches](https://github.com/speaches-ai/speaches) als eigenständiger Whisper-Container, OpenAI-API-kompatibel — auch von Home Assistant, n8n usw. nutzbar

## Schnellstart für Endnutzer

1. Auf der **Releases-Seite** des Repos den neuesten Installer herunterladen
2. `VoiceAgend-win-Setup.exe` ausführen — App installiert sich nach `%LocalAppData%\VoiceAgend`
3. App startet automatisch ins Tray; Settings-Fenster öffnet sich beim ersten Start
4. **Server-URL** und **API-Key** vom Server-Admin eintragen
5. Mikrofon und Hotkey wählen (Default: `Strg+Shift+R`)
6. Hotkey drücken → Aufnahme. Nochmal drücken → Transkript landet in Zwischenablage / aktivem Fenster / als Notification

Auto-Updates laufen im Hintergrund: bei neuer Release-Version erscheint im Settings-Tab ein „Update installieren"-Button.

## Server-Setup (für Self-Hoster)

### Voraussetzungen

- TrueNAS SCALE 24.10+ (oder beliebiges Linux mit Docker)
- 16+ GB RAM (Whisper-`medium` braucht ~3 GB, `large-v3` ~6 GB)
- Reverse Proxy mit WebSocket-Support (z. B. nginx-proxymanager) und HTTPS-Zertifikat
- Optional: NVIDIA-GPU (Turing+ / RTX 20xx aufwärts) für deutlich schnellere Transkription. Ältere Karten wie Pascal werden vom in TrueNAS 24.10+ ausgelieferten Open-Source-Treiber **nicht** unterstützt — CPU-Modus läuft mit `medium` auf einem 6-Kerner ungefähr in Echtzeit, was für Diktat ausreicht.

### 1. Datasets anlegen (TrueNAS)

In der Apps-Konvention unter dem Pool deiner Wahl:

```
<pool>/Applications/voiceagend/data        Owner: apps:apps   (UID/GID 568)
<pool>/Applications/speaches/cache         Owner: 1000:1000
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

Image bauen und nach GHCR pushen — automatisch via [`.github/workflows/build-server.yml`](.github/workflows/build-server.yml). Image-Adresse: `ghcr.io/<dein-user>/voiceagend-server:latest-cpu`.

YAML in [`server/docker-compose.truenas.yml`](server/docker-compose.truenas.yml) anpassen:
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

- Domain im Browser öffnen → mit `Admin` und gesetztem Passwort einloggen
- **API-Keys** anlegen (einer pro Gerät empfohlen)
- Den `va_…`-Key kopieren — wird nur einmal angezeigt
- Im Windows-Client Server-URL und API-Key eintragen

## Code Signing

Aktuell ist der Installer **nicht signiert**. Windows SmartScreen warnt — über
**„Weitere Informationen" → „Trotzdem ausführen"** lässt sich der Installer starten.

Eine Bewerbung bei der [SignPath Foundation](https://signpath.org/apply) für kostenloses
OSS-Code-Signing läuft. Sobald genehmigt, werden zukünftige Releases signiert ausgeliefert.

## Lizenzierung

Pro Komponente unterschiedlich — siehe [LICENSE](LICENSE):

- **`client/`** — [GPL-3.0-or-later](client/LICENSE)
- **`server/`** — [AGPL-3.0-or-later](server/LICENSE)
- **`whisper/`** — nur Compose-Konfiguration

Forks und Modifikationen müssen unter denselben Lizenzen weitergegeben werden.
