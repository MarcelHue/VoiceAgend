using System.IO;
using System.Text.Json;
using VoiceAgend.App.Models;

namespace VoiceAgend.App.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string FilePath { get; }

    public SettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceAgend");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            Logger.Info($"Settings: no file at {FilePath}, using defaults");
            return new AppSettings();
        }
        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            if (loaded is null)
            {
                Logger.Warn($"Settings: deserialized to null, using defaults");
                return new AppSettings();
            }
            return loaded;
        }
        catch (Exception ex)
        {
            Logger.Error($"Settings: load failed from {FilePath}", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings s)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Error($"Settings: save failed to {FilePath}", ex);
        }
    }
}
