using System.Media;
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
    private readonly Func<AppSettings> _settingsProvider;

    public event Action<string>? StatusChanged;
    public event Action? StateChanged;
    public event Action<string>? TranscriptReceived;

    public string LastTranscript { get; private set; } = "";

    public bool IsRecording => _audio.IsRecording;
    public bool IsBusy { get; private set; }

    public RecordingCoordinator(
        AudioCaptureService audio,
        TranscriptionClient client,
        OutputService output,
        Func<AppSettings> settingsProvider)
    {
        _audio = audio;
        _client = client;
        _output = output;
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
                if (s.PlaySoundOnStart) PlayStart();
                StatusChanged?.Invoke("Aufnahme läuft…");
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("Mic start failed", ex);
                StatusChanged?.Invoke($"Mic-Fehler: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        // 2. Druck: stoppen + senden
        IsBusy = true;
        StateChanged?.Invoke();
        try
        {
            var audio = _audio.Stop();
            if (s.PlaySoundOnStop) PlayStop();
            StateChanged?.Invoke();

            if (audio.Length < 1024)
            {
                StatusChanged?.Invoke("Zu kurz, verworfen.");
                return;
            }

            StatusChanged?.Invoke("Sende…");
            var result = await _client.TranscribeAsync(
                s.ServerUrl, s.ApiKey,
                string.IsNullOrWhiteSpace(s.Language) ? null : s.Language,
                audio,
                new Progress<string>(p => StatusChanged?.Invoke(p)));

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                StatusChanged?.Invoke("Leeres Transkript.");
                return;
            }

            LastTranscript = result.Text;
            TranscriptReceived?.Invoke(result.Text);
            _output.Dispatch(s.OutputMode, result.Text, s.ShowToastOnResult);
            StatusChanged?.Invoke($"Fertig ({result.ProcessingMs} ms).");
        }
        catch (Exception ex)
        {
            Logger.Error("Transcription failed", ex);
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            StatusChanged?.Invoke($"Fehler: {ex.GetType().Name}: {msg}");
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
        }
    }

    private static void PlayStart() => SystemSounds.Asterisk.Play();
    private static void PlayStop() => SystemSounds.Beep.Play();
}
