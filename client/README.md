# VoiceAgend Client

## Projects

- `VoiceAgend.Cli/` — CLI test client (phase 1 ✓)
- `VoiceAgend.App/` — WinUI 3 tray application

## Prerequisites

- .NET 9 SDK
- Windows 10 1809+ / Windows 11
- Windows App SDK Runtime 1.6 (referenced via NuGet at build time, but **not** auto-installed at runtime — see "Distribution")
- Toast notifications require the app to be registered. Windows asks on first launch; an MSIX package is the proper way to register permanently.

## First-time setup

1. **Provide tray icons** in `VoiceAgend.App/Assets/`:
   - `TrayIdle.ico`
   - `TrayRecording.ico`
   See [Assets/README.txt](VoiceAgend.App/Assets/README.txt) for a quick recipe.

2. **Open the solution in Rider** (`VoiceAgend.sln`), set `VoiceAgend.App` as the startup project.

3. **Run** (F5 / Shift+F10). The settings window opens automatically on first launch because no API key is configured yet.

## Usage

- **Settings window:** configure server URL, API key, language, microphone, hotkey, output mode.
- **Hotkey recording:** press the button → press the desired key combination. Default: `Ctrl+Shift+R`.
- **Toggle recording:** press the hotkey to start (tray icon turns red, optional sound). Press again → recording stops, audio is sent, the transcript appears according to the output mode.
- **Tray:** left-click opens the settings window. Right-click → menu with toggle / settings / quit.

## Output modes

| Mode | Effect |
|---|---|
| **Clipboard** | Text is copied to the clipboard (Ctrl+V to paste). Optional toast. |
| **Type into active window** | Text is typed via SendInput into the currently focused window. Note: in protected fields (password boxes, certain browser inputs) this can be blocked. The clipboard is filled in parallel as a fallback. |
| **Notification only** | Toast with a transcript preview, nothing else. |

## Settings file

`%AppData%\VoiceAgend\settings.json`. You can edit it by hand; it is loaded on the next start.

## Architecture

- **`AudioCaptureService`** — NAudio + Concentus, identical to the CLI.
- **`TranscriptionClient`** — WebSocket to the server, sends Opus/Ogg, reads JSON responses.
- **`HotkeyManager`** — global Win32 hotkey via `RegisterHotKey`. Owns its message loop on a background thread.
- **`OutputService`** — Clipboard / SendInput / Toast.
- **`SoundService`** — NAudio-based playback of Windows system WAVs with volume control.
- **`RecordingCoordinator`** — toggle logic: 1st press starts, 2nd press stops + sends + outputs.
- **`SettingsStore`** — JSON persistence.
- **`UpdateService`** — Velopack auto-update against GitHub Releases.
- **`AutoStartService`** — `HKCU\…\Run` registry entry.
- **`LocalizationService`** — runtime translation via `Localization/strings.<lang>.json`.

## Distribution & auto-update (Velopack + GitHub Releases)

Every push to `main` triggers [build-app.yml](../.github/workflows/build-app.yml), which produces
a self-contained `.exe`, packs it with Velopack, and publishes a GitHub Release.
Versioning: `1.0.<run_number>`.

**End-user installation:**
1. On the repo's Releases page, open the latest release.
2. Download `VoiceAgend-win-Setup.exe` and run it — installs to `%LocalAppData%\VoiceAgend`.
3. The app starts and the tray icon appears.

**Auto-update:**
- On launch the app checks for new releases in the background.
- If one is available, the settings page shows an **"Install update"** button.
- Clicking it lets Velopack download the delta, apply it, and restart the app.
- Manual check: **"Check for updates"** button.

**Note:** auto-update only works for Velopack-installed copies. Builds run from Rider show
"Not installed via the installer — auto-update disabled."

## Localization

Two language profiles ship in `VoiceAgend.App/Localization/`:
- `strings.de.json` — German
- `strings.en.json` — English

The picker in **Settings → Application language** switches at runtime. To add another
language, drop a `strings.<code>.json` next to the existing files and add the code to
`LocalizationService.AvailableLanguages`. Missing keys fall back to German; unknown keys
fall back to the key name itself.

## Known limitations

- **Toggle, not push-to-talk** — `RegisterHotKey` only delivers "pressed" events. True PTT needs a `WH_KEYBOARD_LL` hook (planned).
- **No fully integrated Auto-Start UX** — the registry entry is set, but Windows policies on some systems can override.
- **One microphone at runtime** — switching requires a settings save (no live reconfiguration).

---
---

# VoiceAgend Client (Deutsch)

## Projekte

- `VoiceAgend.Cli/` — CLI-Testclient (Phase 1 ✓)
- `VoiceAgend.App/` — WinUI-3-Tray-App

## Voraussetzungen

- .NET 9 SDK
- Windows 10 1809+ / Windows 11
- Windows App SDK Runtime 1.6 (wird via NuGet beim Bauen referenziert, zur Laufzeit auf dem Zielsystem aber **nicht** automatisch installiert — siehe „Auslieferung")
- Toast-Notifications: App muss registriert sein. Erststart fragt Windows danach; sauber dauerhaft via MSIX-Paket.

## Erstes Setup

1. **Tray-Icons bereitstellen** in `VoiceAgend.App/Assets/`:
   - `TrayIdle.ico`
   - `TrayRecording.ico`
   Quick-Setup siehe [Assets/README.txt](VoiceAgend.App/Assets/README.txt).

2. **Solution in Rider öffnen** (`VoiceAgend.sln`), `VoiceAgend.App` als Startup-Projekt setzen.

3. **Run** (F5 / Shift+F10). Beim Erststart öffnet sich automatisch das Settings-Fenster, weil noch kein API-Key gesetzt ist.

## Bedienung

- **Settings-Fenster:** Server-URL, API-Key, Sprache, Mikrofon, Hotkey, Output-Modus konfigurieren.
- **Hotkey-Aufzeichnung:** Button drücken → gewünschte Tasten-Kombination drücken. Standard: `Strg+Shift+R`.
- **Toggle-Aufnahme:** Hotkey drücken startet die Aufnahme (Tray-Icon wird rot, optional Sound). Nochmal drücken → Aufnahme stoppt, wird gesendet, Ergebnis je nach Output-Modus.
- **Tray:** Linksklick öffnet das Settings-Fenster, Rechtsklick → Menü mit Toggle/Einstellungen/Beenden.

## Output-Modi

| Modus | Effekt |
|---|---|
| **Zwischenablage** | Text wird in Clipboard kopiert (Strg+V einfügen). Optional Toast. |
| **Direkt tippen** | Text wird via SendInput in das aktive Fenster getippt. Achtung: in „geschützten" Eingabefeldern (Passwort-Boxen, manche Browser) kann das blockiert sein. Clipboard wird parallel mit befüllt. |
| **Nur Benachrichtigung** | Toast mit Transkript-Vorschau, sonst nichts. |

## Settings-Datei

`%AppData%\VoiceAgend\settings.json`. Manuell editierbar, wird beim nächsten App-Start gelesen.

## Auslieferung & Auto-Update (Velopack + GitHub Releases)

Bei jedem Push auf `main` baut die Action [build-app.yml](../.github/workflows/build-app.yml)
automatisch eine self-contained `.exe`, packt sie mit Velopack und veröffentlicht sie als
GitHub Release. Versionsschema: `1.0.<run_number>`.

**Endnutzer-Installation:**
1. Auf der Releases-Seite den neuesten Release öffnen.
2. `VoiceAgend-win-Setup.exe` herunterladen und ausführen — installiert nach `%LocalAppData%\VoiceAgend`.
3. App startet, Tray-Icon erscheint.

**Auto-Update:**
- Beim Start prüft die App im Hintergrund auf neue Releases.
- Falls verfügbar: Button **„Update installieren"** in den Einstellungen.
- Klick → Velopack lädt das Delta, applyt es, startet die App neu.
- Manuell: **„Auf Updates prüfen"**.

**Hinweis:** Auto-Update funktioniert nur bei Velopack-Install. Builds aus Rider zeigen
„Nicht über Installer installiert — Auto-Update deaktiviert.".

## Lokalisierung

Zwei Sprach-Profile liegen in `VoiceAgend.App/Localization/`:
- `strings.de.json` — Deutsch
- `strings.en.json` — Englisch

Umschalten via **Einstellungen → Anwendungssprache** zur Laufzeit. Weitere Sprachen: einfach
eine `strings.<code>.json` daneben legen und den Code in `LocalizationService.AvailableLanguages`
ergänzen. Fehlende Keys fallen auf Deutsch zurück; unbekannte Keys auf den Key-Namen selbst.

## Bekannte Einschränkungen

- **Toggle, kein Push-to-Talk** — `RegisterHotKey` liefert nur „pressed". Echtes PTT braucht `WH_KEYBOARD_LL`-Hook (folgt).
- **Auto-Start nicht in jedem System-Layout integriert** — Registry-Eintrag wird gesetzt, manche Group-Policies überschreiben das.
- **Nur ein Mikrofon zur Laufzeit** — Wechsel braucht Settings-Save (kein Hot-Reload).
