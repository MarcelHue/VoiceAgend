using System.IO;
using System.Text.Json;

namespace VoiceAgend.App.Services;

public sealed class LocalizationService
{
    private Dictionary<string, string> _strings = new();
    public string CurrentLanguage { get; private set; } = "de";

    public event Action? LanguageChanged;

    public static IReadOnlyList<(string Code, string Display)> AvailableLanguages { get; } = new[]
    {
        ("de", "Deutsch"),
        ("en", "English"),
    };

    public void Load(string lang)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Localization");
        var primary = Path.Combine(dir, $"strings.{lang}.json");
        var fallback = Path.Combine(dir, "strings.de.json");
        var path = File.Exists(primary) ? primary : fallback;
        try
        {
            var json = File.ReadAllText(path);
            _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            CurrentLanguage = File.Exists(primary) ? lang : "de";
            Logger.Info($"Localization loaded: {CurrentLanguage} ({_strings.Count} keys)");
        }
        catch (Exception ex)
        {
            Logger.Error("Localization load", ex);
            _strings = new();
        }
        LanguageChanged?.Invoke();
    }

    /// <summary>Resolved string oder Fallback (bei fehlendem Key: der Key selbst).</summary>
    public string T(string key, string? fallback = null) =>
        _strings.TryGetValue(key, out var v) ? v : (fallback ?? key);
}
