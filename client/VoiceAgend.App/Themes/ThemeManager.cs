using Microsoft.UI.Xaml;

namespace VoiceAgend.App.Themes;

/// <summary>
/// Wechselt das WinUI-ElementTheme (Light/Dark) auf der Content-Root jedes Fensters.
/// XAML referenziert Theme-abhängige Brushes via {ThemeResource VABgBrush} usw. —
/// das markup-extension löst beim Theme-Wechsel automatisch neu auf.
/// </summary>
public static class ThemeManager
{
    public static event Action? Changed;
    public static string Current { get; private set; } = "dark";

    private static readonly List<WeakReference<FrameworkElement>> Roots = new();

    /// <summary>Registriert einen Window-Content-Root, der bei Theme-Wechsel mit-umgeschaltet wird.</summary>
    public static void RegisterRoot(FrameworkElement root)
    {
        Roots.Add(new WeakReference<FrameworkElement>(root));
        root.RequestedTheme = Resolve(Current);
    }

    public static void Apply(string mode)
    {
        Current = string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        var theme = Resolve(Current);
        Roots.RemoveAll(wr => !wr.TryGetTarget(out _));
        foreach (var wr in Roots)
            if (wr.TryGetTarget(out var fe)) fe.RequestedTheme = theme;
        Changed?.Invoke();
    }

    private static ElementTheme Resolve(string mode) =>
        mode == "light" ? ElementTheme.Light : ElementTheme.Dark;
}
