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
        if (lower.Contains("aufnahme")) Show(HudState.Recording, status);
        else if (lower.Contains("sende") || lower.Contains("verbinde")) Show(HudState.Sending, status);
        else if (lower.Contains("warte") || lower.Contains("verarbeit")) Show(HudState.Processing, status);
        else if (lower.Contains("fertig")) Show(HudState.Done, status);
        else if (lower.Contains("fehler") || lower.Contains("mic-fehler")) Show(HudState.Error, status);
        else if (lower.Contains("kurz")) Show(HudState.Error, status);
        // sonst: ignorieren (z. B. "Gespeichert" aus dem Settings-Fenster)
    }
}
