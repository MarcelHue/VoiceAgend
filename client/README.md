# VoiceAgend Client

## Projekte

- `VoiceAgend.Cli/` — CLI-Testclient (Phase 1 ✓)
- `VoiceAgend.App/` — WinUI-3-Tray-App (Phase 2 — Iteration 1)

## Voraussetzungen

- .NET 9 SDK
- Windows 10 1809+ / Windows 11
- Windows App SDK Runtime 1.6 (wird bei `dotnet restore` automatisch via NuGet referenziert,
  zur Laufzeit auf dem Zielsystem aber **nicht** automatisch installiert — siehe „Auslieferung")
- Für Toast-Notifications muss die App registriert sein. Erststart fragt Windows danach;
  alternativ später per MSIX.

## Erstes Setup

1. **Tray-Icons bereitstellen** in `VoiceAgend.App/Assets/`:
   - `TrayIdle.ico`
   - `TrayRecording.ico`
   Siehe [Assets/README.txt](VoiceAgend.App/Assets/README.txt) für Quick-Setup.

2. **Solution in Rider öffnen** (`VoiceAgend.sln`), `VoiceAgend.App` als Startup-Projekt setzen.

3. **Run** (F5 / Shift+F10). Beim Erststart öffnet sich automatisch das Settings-Fenster,
   weil noch kein API-Key gesetzt ist.

## Bedienung

- **Settings-Fenster:** Server-URL, API-Key, Sprache, Mikrofon, Hotkey, Output-Modus konfigurieren.
- **Hotkey-Aufzeichnung:** Button drücken → gewünschte Tasten-Kombination drücken.
  Standard: `Strg+Shift+R`.
- **Toggle-Aufnahme:** Hotkey drücken startet die Aufnahme (Tray-Icon wechselt zu rot,
  optional Sound). Nochmal drücken → Aufnahme stoppt, wird gesendet, Ergebnis kommt zurück
  je nach Output-Modus.
- **Tray:** Linksklick öffnet das Settings-Fenster, Rechtsklick → Menü mit Toggle/Beenden.

## Output-Modi

| Modus | Effekt |
|---|---|
| **Zwischenablage** | Text wird in Clipboard kopiert (Strg+V einfügen). Optional Toast. |
| **Direkt tippen** | Text wird via SendInput in das aktuell aktive Fenster getippt. Achtung: in „geschützten" Eingabefeldern (Passwort-Boxen, manche Browser-Felder) kann das blockiert sein. Clipboard wird parallel mit befüllt. |
| **Nur Benachrichtigung** | Toast mit Transkript-Vorschau, sonst nichts. |

## Settings-Datei

`%AppData%\VoiceAgend\settings.json`. Kannst du auch von Hand editieren, wird beim nächsten App-Start gelesen.

## Architektur

- **`AudioCaptureService`** — NAudio + Concentus, identisch zur CLI.
- **`TranscriptionClient`** — WebSocket nach Server, schickt Opus/Ogg, holt JSON-Antwort.
- **`HotkeyManager`** — globaler Win32-Hotkey via `RegisterHotKey`. Eigene Message-Loop auf Background-Thread.
- **`OutputService`** — Clipboard / SendInput / Toast.
- **`RecordingCoordinator`** — Toggle-Logic: 1. Druck = Start, 2. Druck = Stopp+Senden+Output.
- **`SettingsStore`** — JSON-Persistenz.

## Auslieferung & Auto-Update (Velopack + GitHub Releases)

Bei jedem Push auf `main` baut die GitHub Action [build-app.yml](../.github/workflows/build-app.yml)
automatisch eine self-contained `.exe`, packt sie mit Velopack und veröffentlicht sie als
GitHub Release. Versionsschema: `1.0.<run_number>`.

**Endnutzer-Installation (Erstinstallation):**
1. Auf der Releases-Seite des Repos den neuesten Release öffnen.
2. `VoiceAgend-win-Setup.exe` herunterladen und ausführen — installiert in `%LocalAppData%\VoiceAgend`.
3. App startet, Tray-Icon erscheint.

**Auto-Update:**
- Beim Start prüft die App im Hintergrund auf neue Releases.
- Falls verfügbar: in den Einstellungen erscheint **„Update installieren"**.
- Klick → Velopack lädt das Delta, applyt es und startet die App neu.
- Manuell prüfbar via **„Auf Updates prüfen"**-Button.

**Achtung:** Auto-Update funktioniert nur bei der Velopack-Installation. Direkt aus Rider gestartete
Builds (Dev-Modus) zeigen „Nicht über Installer installiert — Auto-Update deaktiviert.".

## Bekannte Einschränkungen Iteration 1

- **Toggle, kein Push-to-Talk** — `RegisterHotKey` liefert nur „pressed". Echtes PTT braucht
  `WH_KEYBOARD_LL`-Hook (folgt in nächster Iteration).
- **Kein Auto-Start** — manuell starten oder eine Verknüpfung in `shell:startup` legen.
- **Keine Server-Health-Anzeige** im Settings-Fenster (nice-to-have).
- **Nur ein Mikrofon zur Laufzeit** — Wechsel braucht App-Neustart oder Settings-Save.
