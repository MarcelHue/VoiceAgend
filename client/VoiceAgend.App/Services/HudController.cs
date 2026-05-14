using Microsoft.UI.Dispatching;
using VoiceAgend.App.Models;

namespace VoiceAgend.App.Services;

public sealed class HudController
{
    private readonly HudWindow _window;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherQueue _ui;

    public HudController(HudWindow window, Func<AppSettings> settings)
    {
        _window = window;
        _settings = settings;
        _ui = window.DispatcherQueue;
    }

    private void OnUi(Action a)
    {
        if (_ui.HasThreadAccess) a();
        else _ui.TryEnqueue(() => a());
    }

    public void ApplyPosition() => OnUi(() =>
    {
        var s = _settings();
        _window.ApplyPosition(s.HudPosition, s.HudMargin);
    });

    public void Show(HudState state, string text) => OnUi(() =>
    {
        if (!_settings().HudEnabled) { _window.Hide(); return; }
        var s = _settings();
        _window.ApplyPosition(s.HudPosition, s.HudMargin);
        _window.Show(state, text);
    });

    public void Hide() => OnUi(() => _window.Hide());

    public void OnStatus(string status)
    {
        // Mappt freie Status-Strings auf HUD-States.
        var lower = status.ToLowerInvariant();
        if (lower.Contains("aufnahme läuft") || lower.Contains("recording…")
            || lower == "aufnahme läuft…" || lower == "recording")
            Show(HudState.Recording, status);
        else if (lower.Contains("warte") || lower.Contains("verarbeit") || lower.Contains("transkrip")
                 || lower.Contains("waiting") || lower.Contains("transcrip"))
            Show(HudState.Processing, status);
        else if (lower.Contains("sende") || lower.Contains("verbinde")
                 || lower.Contains("sending") || lower.Contains("connecting"))
            Show(HudState.Sending, status);
        else if (lower.Contains("fertig") || lower.Contains("done"))
            Show(HudState.Done, status);
        else if (lower.Contains("kurz") || lower.Contains("verworfen")
                 || lower.Contains("too short") || lower.Contains("discarded"))
            Show(HudState.Error, status);
        else if (lower.Contains("fehler") || lower.Contains("error"))
            Show(HudState.Error, status);
        // sonst: ignorieren (z. B. "Gespeichert" aus dem Settings-Fenster)
    }
}
