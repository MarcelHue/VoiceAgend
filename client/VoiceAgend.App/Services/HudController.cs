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
        // WICHTIG: Reihenfolge zählt — "Leeres Transkript" enthält "transkrip",
        // darum muss "Discarded/Error" VOR "Processing" geprüft werden.
        var lower = status.ToLowerInvariant();

        // 1) Discarded / Empty (terminal, HUD blendet aus)
        if (lower.Contains("kurz") || lower.Contains("verworfen") || lower.Contains("leer")
            || lower.Contains("too short") || lower.Contains("discarded") || lower.Contains("empty"))
        {
            Show(HudState.Error, status);
            return;
        }
        // 2) Fehler (terminal)
        if (lower.Contains("fehler") || lower.Contains("error") || lower.Contains("failed"))
        {
            Show(HudState.Error, status);
            return;
        }
        // 3) Fertig (terminal)
        if (lower.Contains("fertig") || lower.Contains("done"))
        {
            Show(HudState.Done, status);
            return;
        }
        // 4) Recording
        if (lower.Contains("aufnahme läuft") || lower.Contains("recording"))
        {
            Show(HudState.Recording, status);
            return;
        }
        // 5) Sending
        if (lower.Contains("sende") || lower.Contains("verbinde")
            || lower.Contains("sending") || lower.Contains("connecting"))
        {
            Show(HudState.Sending, status);
            return;
        }
        // 6) Processing / Transkription (zuletzt, weil "transkrip" auch in "Leeres Transkript" steckt)
        if (lower.Contains("warte") || lower.Contains("verarbeit") || lower.Contains("transkrip")
            || lower.Contains("waiting") || lower.Contains("transcrib"))
        {
            Show(HudState.Processing, status);
            return;
        }
        // sonst: ignorieren (z. B. "Gespeichert" aus dem Settings-Fenster)
    }
}
