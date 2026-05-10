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
            StatusChanged?.Invoke(string.Format(Loc().T("Status.DoneFmt"), result.ProcessingMs));
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
}
