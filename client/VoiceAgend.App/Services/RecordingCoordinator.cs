using System.IO;
using VoiceAgend.App.Models;

namespace VoiceAgend.App.Services;

/// <summary>
/// Orchestriert Hotkey → Aufnahme → Transkription → Output.
/// Toggle-Modus: erster Druck startet, zweiter Druck stoppt + sendet.
/// </summary>
public sealed class RecordingCoordinator
{
    private readonly AudioCaptureService _audio;
    private readonly TranscriptionClient _client;
    private readonly OutputService _output;
    private readonly SoundService _sound;
    private readonly Func<AppSettings> _settingsProvider;

    public event Action<string>? StatusChanged;
    public event Action? StateChanged;
    public event Action<string>? TranscriptReceived;

    public string LastTranscript { get; private set; } = "";

    private static LocalizationService Loc() => App.Current.Loc;

    public bool IsRecording => _audio.IsRecording;
    public bool IsBusy { get; private set; }

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

            StatusChanged?.Invoke(Loc().T("Status.Sending"));
            var result = await _client.TranscribeAsync(
                s.ServerUrl, s.ApiKey,
                string.IsNullOrWhiteSpace(s.Language) ? null : s.Language,
                audio,
                new Progress<string>(p => StatusChanged?.Invoke(p)));

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                StatusChanged?.Invoke(Loc().T("Status.EmptyTranscript"));
                return;
            }

            LastTranscript = result.Text;
            TranscriptReceived?.Invoke(result.Text);
            _output.Dispatch(s.OutputMode, result.Text, s.ShowToastOnResult);
            _sound.Play(s.SoundOnDone, s.SoundVolume);

            // Status mit erkannter Sprache anzeigen — wichtig bei Auto-Modus,
            // damit der User sofort sieht ob Whisper richtig oder falsch detected hat.
            var langTag = string.IsNullOrWhiteSpace(result.Language) ? "" : result.Language.ToUpperInvariant();
            var msg = string.IsNullOrEmpty(langTag)
                ? string.Format(Loc().T("Status.DoneFmt"), result.ProcessingMs)
                : string.Format(Loc().T("Status.DoneWithLangFmt"), result.ProcessingMs, langTag);
            StatusChanged?.Invoke(msg);
        }
        catch (Exception ex)
        {
            Logger.Error("Transcription failed", ex);
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

            StatusChanged?.Invoke(Loc().T("Status.Sending"));
            var result = await _client.TranscribeAsync(
                s.ServerUrl, s.ApiKey,
                string.IsNullOrWhiteSpace(s.Language) ? null : s.Language,
                audio,
                new Progress<string>(p => StatusChanged?.Invoke(p)));

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                StatusChanged?.Invoke(Loc().T("Status.EmptyTranscript"));
                return;
            }

            LastTranscript = result.Text;
            TranscriptReceived?.Invoke(result.Text);
            _output.Dispatch(s.OutputMode, result.Text, s.ShowToastOnResult);
            _sound.Play(s.SoundOnDone, s.SoundVolume);

            var langTag = string.IsNullOrWhiteSpace(result.Language) ? "" : result.Language.ToUpperInvariant();
            var msg = string.IsNullOrEmpty(langTag)
                ? string.Format(Loc().T("Status.DoneFmt"), result.ProcessingMs)
                : string.Format(Loc().T("Status.DoneWithLangFmt"), result.ProcessingMs, langTag);
            StatusChanged?.Invoke(msg);
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
}
