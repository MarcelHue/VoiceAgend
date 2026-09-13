using System.IO;
using VoiceAgend.App.Models;

namespace VoiceAgend.App.Services;

/// <summary>
/// Orchestriert Hotkey → Aufnahme → Transkription → Output.
/// Toggle-Modus: erster Druck startet, zweiter Druck stoppt + sendet.
/// Schlägt das Senden fehl, bleibt die Aufnahme als "ausstehend" erhalten (RAM + Datei),
/// damit sie später erneut gesendet werden kann, statt neu gesprochen werden zu müssen.
/// </summary>
public sealed class RecordingCoordinator
{
    private readonly AudioCaptureService _audio;
    private readonly TranscriptionClient _client;
    private readonly OutputService _output;
    private readonly SoundService _sound;
    private readonly Func<AppSettings> _settingsProvider;

    // Zuletzt fehlgeschlagene Aufnahme (Ogg/Opus). Überlebt einen App-Neustart über
    // PendingPath. Ein Slot: eine neue fehlgeschlagene Aufnahme ersetzt die alte, eine
    // neue ERFOLGREICHE lässt sie stehen (sonst wäre sie beim "Server wieder da?"-Test weg).
    private byte[]? _pending;
    private static readonly string PendingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceAgend", "pending-recording.ogg");

    public event Action<string>? StatusChanged;
    public event Action? StateChanged;
    public event Action<string>? TranscriptReceived;
    /// <summary>Gefeuert, wenn eine Aufnahme zum erneuten Senden bereitsteht oder wegfällt.</summary>
    public event Action? PendingRetryChanged;

    public string LastTranscript { get; private set; } = "";

    private static LocalizationService Loc() => App.Current.Loc;

    public bool IsRecording => _audio.IsRecording;
    public bool IsBusy { get; private set; }
    public bool HasPendingRetry => _pending is not null;

    public RecordingCoordinator(
        AudioCaptureService audio,
        TranscriptionClient client,
        OutputService output,
        SoundService sound,
        Func<AppSettings> settingsProvider)
    {
        _audio = audio;
        _client = client;
        _output = output;
        _sound = sound;
        _settingsProvider = settingsProvider;
        LoadPending();
    }

    /// <summary>Hotkey-Trigger.</summary>
    public async Task ToggleAsync()
    {
        if (IsBusy) return;
        var s = _settingsProvider();

        if (!_audio.IsRecording)
        {
            try
            {
                Logger.Info($"Start recording, device={s.MicDeviceNumber}");
                _audio.Start(s.MicDeviceNumber);
                _sound.Play(s.SoundOnStart, s.SoundVolume);
                StatusChanged?.Invoke(Loc().T("Status.Recording"));
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("Mic start failed", ex);
                _sound.Play(s.SoundOnError, s.SoundVolume);
                StatusChanged?.Invoke(string.Format(Loc().T("Status.MicErrorFmt"), $"{ex.GetType().Name}: {ex.Message}"));
            }
            return;
        }

        // 2. Druck: stoppen + senden
        IsBusy = true;
        StateChanged?.Invoke();
        try
        {
            var audio = _audio.Stop();
            _sound.Play(s.SoundOnStop, s.SoundVolume);
            StateChanged?.Invoke();

            if (audio.Length < 1024)
            {
                StatusChanged?.Invoke(Loc().T("Status.TooShort"));
                return;
            }

            await SendAsync(audio, s);
        }
        catch (Exception ex)
        {
            Logger.Error("Stop recording failed", ex);
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            _sound.Play(s.SoundOnError, s.SoundVolume);
            StatusChanged?.Invoke(string.Format(Loc().T("Status.ErrorFmt"), $"{ex.GetType().Name}: {msg}"));
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Sendet die zuletzt fehlgeschlagene Aufnahme erneut — mit der AKTUELL eingestellten
    /// Sprache, damit man vorher z. B. noch die Sprache korrigieren kann.
    /// </summary>
    public async Task RetryPendingAsync()
    {
        if (IsBusy || _audio.IsRecording || _pending is null) return;
        var s = _settingsProvider();
        IsBusy = true;
        StateChanged?.Invoke();
        try
        {
            Logger.Info($"Retry pending recording ({_pending.Length} bytes)");
            await SendAsync(_pending, s, isRetry: true);
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Verwirft die ausstehende Aufnahme endgültig.</summary>
    public void DiscardPending()
    {
        if (_pending is null) return;
        ClearPending();
        StatusChanged?.Invoke(Loc().T("Status.PendingDiscarded"));
    }

    /// <summary>
    /// Bricht eine laufende Aufnahme ab und VERWIRFT das Audio (kein Upload). Wird von der
    /// Sprachwechsel-Geste (Hotkey halten + scrollen) benutzt: dort soll nur die Sprache
    /// geändert, aber nichts transkribiert werden. No-op, wenn gerade nicht aufgenommen wird.
    /// Der Status-Text enthält bewusst KEINE HUD-Keywords — das HUD steuert die Geste selbst.
    /// </summary>
    public void Cancel()
    {
        if (!_audio.IsRecording) return;
        try { _audio.Stop(); } catch (Exception ex) { Logger.Warn("Cancel stop: " + ex.Message); }
        StatusChanged?.Invoke(Loc().T("Status.LangSwitchGesture"));
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Lädt eine Audio-Datei vom Pfad, komprimiert WAV vorher zu Opus (bandbreitenschonend),
    /// und sendet sie wie eine normale Aufnahme an den Server.
    /// </summary>
    public async Task TranscribeFileAsync(string filePath)
    {
        if (IsBusy || _audio.IsRecording) return;
        var s = _settingsProvider();
        IsBusy = true;
        StateChanged?.Invoke();
        try
        {
            Logger.Info($"Transcribe file: {filePath}");
            StatusChanged?.Invoke(Loc().T("Status.Sending"));

            var raw = await File.ReadAllBytesAsync(filePath);
            byte[] audio;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            // WAV ist unkomprimiert → vor dem Upload zu Opus encoden.
            // Alle anderen Formate (mp3, m4a, ogg, opus, flac, aac, webm, …) sind
            // bereits komprimiert; Speaches dekodiert sie serverseitig via ffmpeg.
            if (ext is ".wav" or ".pcm")
            {
                StatusChanged?.Invoke(Loc().T("Status.Compressing"));
                audio = await Task.Run(() => AudioCaptureService.EncodeWavToOpus(raw));
            }
            else
            {
                audio = raw;
            }

            if (audio.Length < 1024)
            {
                StatusChanged?.Invoke(Loc().T("Status.TooShort"));
                return;
            }

            await SendAsync(audio, s);
        }
        catch (Exception ex)
        {
            Logger.Error("File transcription failed", ex);
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            _sound.Play(s.SoundOnError, s.SoundVolume);
            StatusChanged?.Invoke(string.Format(Loc().T("Status.ErrorFmt"), $"{ex.GetType().Name}: {msg}"));
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Gemeinsamer Sende-Pfad für Live-Aufnahme, Datei-Upload und Retry. Bei Fehler oder
    /// leerem Ergebnis wird das Audio als ausstehend gemerkt; ein erfolgreicher Retry räumt
    /// es wieder weg.
    /// </summary>
    private async Task SendAsync(byte[] audio, AppSettings s, bool isRetry = false)
    {
        StatusChanged?.Invoke(Loc().T("Status.Sending"));
        TranscriptionClient.Result result;
        try
        {
            result = await _client.TranscribeAsync(
                s.ServerUrl, s.ApiKey,
                string.IsNullOrWhiteSpace(s.Language) ? null : s.Language,
                audio,
                new Progress<string>(p => StatusChanged?.Invoke(p)));
        }
        catch (Exception ex)
        {
            Logger.Error("Transcription failed", ex);
            KeepPending(audio);
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            _sound.Play(s.SoundOnError, s.SoundVolume);
            StatusChanged?.Invoke(string.Format(Loc().T("Status.ErrorRetryFmt"), $"{ex.GetType().Name}: {msg}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            // Kann an falscher Sprache/zu leisem Mic liegen — Retry mit anderer Sprache
            // soll möglich bleiben.
            KeepPending(audio);
            StatusChanged?.Invoke(Loc().T("Status.EmptyTranscript"));
            return;
        }

        if (isRetry) ClearPending();

        LastTranscript = result.Text;
        TranscriptReceived?.Invoke(result.Text);
        _output.Dispatch(s.OutputMode, result.Text, s.ShowToastOnResult);
        _sound.Play(s.SoundOnDone, s.SoundVolume);

        // Status mit erkannter Sprache anzeigen — wichtig bei Auto-Modus,
        // damit der User sofort sieht ob Whisper richtig oder falsch detected hat.
        var langTag = string.IsNullOrWhiteSpace(result.Language) ? "" : result.Language.ToUpperInvariant();
        var done = string.IsNullOrEmpty(langTag)
            ? string.Format(Loc().T("Status.DoneFmt"), result.ProcessingMs)
            : string.Format(Loc().T("Status.DoneWithLangFmt"), result.ProcessingMs, langTag);
        StatusChanged?.Invoke(done);
    }

    private void KeepPending(byte[] audio)
    {
        _pending = audio;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
            File.WriteAllBytes(PendingPath, audio);
        }
        catch (Exception ex) { Logger.Warn("Pending recording save: " + ex.Message); }
        PendingRetryChanged?.Invoke();
    }

    private void ClearPending()
    {
        _pending = null;
        try { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
        catch (Exception ex) { Logger.Warn("Pending recording delete: " + ex.Message); }
        PendingRetryChanged?.Invoke();
    }

    private void LoadPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return;
            var bytes = File.ReadAllBytes(PendingPath);
            if (bytes.Length >= 1024)
            {
                _pending = bytes;
                Logger.Info($"Pending recording restored ({bytes.Length} bytes)");
            }
            else File.Delete(PendingPath);
        }
        catch (Exception ex) { Logger.Warn("Pending recording load: " + ex.Message); }
    }
}
