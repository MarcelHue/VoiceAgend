# Speaches (Whisper service)

A standalone Whisper container, usable by **VoiceAgend**, **Home Assistant**, **n8n** and any
other OpenAI-API-compatible client.

## Installation on TrueNAS

1. **Create datasets** under `<pool>/Applications/speaches/`:
   - `cache` — for models (~1.5 GB for medium, ~6 GB for large-v3)
   - `config` — optional custom configuration
   Owner: `1000:1000` (the container runs as `ubuntu`, UID 1000).

2. Create the `hub` sub-folder:
   ```bash
   sudo mkdir -p /mnt/<pool>/Applications/speaches/cache/hub
   sudo chown -R 1000:1000 /mnt/<pool>/Applications/speaches/cache
   ```

3. **Apps → Discover → Custom App** and paste [`docker-compose.truenas.yml`](docker-compose.truenas.yml).

4. **Application name:** `speaches`. **Install**.

5. First start: the container pulls the image. The configured default model loads on the first transcription request.

## Test

Web UI in the browser: `http://<truenas-ip>:8001/` (note: a known Gradio + svelte-i18n bug may render the page blank; the API works regardless).

Via curl with an audio file:
```bash
curl http://<truenas-ip>:8001/v1/audio/transcriptions \
  -F file=@/path/to/audio.ogg \
  -F model="Systran/faster-whisper-medium" \
  -F language=de
```
Response: `{ "text": "..." }`.

## Installing models

Models are not auto-loaded. Trigger a download once per model:
```bash
curl -X POST http://<truenas-ip>:8001/v1/models/Systran/faster-whisper-medium
```
The VoiceAgend client also offers a UI for this in the Server profile tab.

## Connecting other services

### VoiceAgend
See `server/` — the VoiceAgend server forwards transcription requests to Speaches.

### Home Assistant
- Install the **OpenAI Whisper Cloud** integration
- Base URL: `http://<truenas-ip>:8001/v1`
- API key: any value (or set `API_KEY` in compose and use the same value)
- Model: `Systran/faster-whisper-medium`

### n8n
- HTTP Request node or OpenAI node:
  - Base URL: `http://<truenas-ip>:8001/v1`
  - Endpoint: `POST /audio/transcriptions`
  - Body: `multipart/form-data` with `file` and `model`

---
---

# Speaches (Whisper-Service) (Deutsch)

Eigenständiger Whisper-Container, der von **VoiceAgend**, **Home Assistant**, **n8n** und allen
anderen OpenAI-API-kompatiblen Clients verwendet werden kann.

## Installation in TrueNAS

1. **Datasets anlegen** unter `<pool>/Applications/speaches/`:
   - `cache` — für Modelle (~1.5 GB für medium, ~6 GB für large-v3)
   - `config` — optionale Custom-Config
   Owner: `1000:1000` (Container läuft als `ubuntu`, UID 1000).

2. `hub`-Unterordner anlegen:
   ```bash
   sudo mkdir -p /mnt/<pool>/Applications/speaches/cache/hub
   sudo chown -R 1000:1000 /mnt/<pool>/Applications/speaches/cache
   ```

3. **Apps → Discover Apps → Custom App** → Inhalt von [`docker-compose.truenas.yml`](docker-compose.truenas.yml) einfügen.

4. **Application Name:** `speaches`. **Install** klicken.

5. Erster Start: Container pullt das Image. Beim ersten Transkriptions-Request wird das Default-Modell geladen.

## Test

Web-UI im Browser: `http://<truenas-ip>:8001/` (Hinweis: ein bekannter Gradio-/svelte-i18n-Bug kann die Seite schwarz lassen; die API funktioniert davon unabhängig).

Per curl mit einer Audio-Datei:
```bash
curl http://<truenas-ip>:8001/v1/audio/transcriptions \
  -F file=@/path/to/audio.ogg \
  -F model="Systran/faster-whisper-medium" \
  -F language=de
```
Antwort: `{ "text": "..." }`.

## Modelle installieren

Modelle werden nicht automatisch geladen, einmalig pro Modell anstoßen:
```bash
curl -X POST http://<truenas-ip>:8001/v1/models/Systran/faster-whisper-medium
```
Der VoiceAgend-Client bietet das auch via UI im Server-Profil-Tab an.

## Anbindung anderer Dienste

### VoiceAgend
Siehe `server/` — der VoiceAgend-Server leitet Transkriptionen an Speaches weiter.

### Home Assistant
- Integration **„OpenAI Whisper Cloud"** installieren
- Base-URL: `http://<truenas-ip>:8001/v1`
- API-Key: beliebig (oder `API_KEY` in der Compose-YAML setzen und denselben Wert eintragen)
- Modell: `Systran/faster-whisper-medium`

### n8n
- HTTP-Request-Node oder OpenAI-Node:
  - Base-URL: `http://<truenas-ip>:8001/v1`
  - Endpoint: `POST /audio/transcriptions`
  - Body: `multipart/form-data` mit `file` und `model`
