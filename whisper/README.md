# Speaches (Whisper-Service)

Eigenständiger Whisper-Container, der von **VoiceAgend**, **Home Assistant**, **n8n** und allen
anderen OpenAI-API-kompatiblen Clients verwendet werden kann.

## Installation in TrueNAS

1. **Datasets anlegen** unter `HDDs/Applications/speaches/`:
   - `cache` — für Modelle (~1.5 GB für medium, ~6 GB für large-v3)
   - `config` — optionale Custom-Config
   ACL-Preset **„Apps"**, Owner `apps:apps` (UID 568).

2. **Apps → Discover → Custom App** → Inhalt von `docker-compose.truenas.yml` einfügen.

3. **Application Name:** `speaches`. **Install** klicken.

4. Erster Start: Container pullt das Image und lädt das `medium`-Modell beim ersten Request.

## Test

Web-UI im Browser: `http://<truenas-ip>:8001/`

Per curl mit einer Audio-Datei:

```bash
curl http://<truenas-ip>:8001/v1/audio/transcriptions \
  -F file=@/path/to/audio.ogg \
  -F model="Systran/faster-whisper-medium" \
  -F language=de
```

Antwort:
```json
{ "text": "..." }
```

## Anbindung anderer Dienste

### VoiceAgend
Siehe `server/` — Server-Container ist auf HTTP-Forward an Speaches umgestellt.

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

## Modelle wechseln

Über die Speaches-Web-UI auf der Models-Seite, oder per API:
```bash
curl -X POST http://<truenas-ip>:8001/v1/models/Systran/faster-distil-whisper-large-v3
```

Aktive Modelle bleiben im RAM. Standard: `medium` ist immer geladen (siehe `WHISPER__MODEL`).
