using System.IO;
using NAudio.Wave;
using VoiceAgend.App.Models;

namespace VoiceAgend.App.Services;

/// <summary>
/// Spielt Hinweistöne über NAudio mit Lautstärkesteuerung.
/// Nutzt Windows-Standard-WAVs (\Media), fällt auf SystemSounds zurück.
/// </summary>
public sealed class SoundService
{
    private static readonly string MediaDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    public void Play(SoundChoice choice, int volumePercent)
    {
        if (choice == SoundChoice.None) return;
        if (choice == SoundChoice.Beep)
        {
            try { System.Media.SystemSounds.Beep.Play(); } catch { }
            return;
        }
        var path = ResolvePath(choice);
        if (path is null || !File.Exists(path))
        {
            try { System.Media.SystemSounds.Beep.Play(); } catch { }
            return;
        }
        // Async abspielen, damit Recording/UI nicht blockiert wird
        _ = Task.Run(() => PlayInternal(path, volumePercent));
    }

    private void PlayInternal(string path, int volumePercent)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            using var output = new WaveOutEvent { Volume = Math.Clamp(volumePercent, 0, 100) / 100f };
            output.Init(reader);
            output.Play();
            // Auf Ende warten — sonst gehen Reader/Output zu früh out of scope
            while (output.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(50);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Sound playback failed ({path}): {ex.Message}");
        }
    }

    private static string? ResolvePath(SoundChoice c)
    {
        // Verschiedene Windows-Versionen haben unterschiedliche Dateinamen.
        // Erste vorhandene Datei aus der Liste wird verwendet.
        var candidates = c switch
        {
            SoundChoice.Chimes => new[] { "chimes.wav" },
            SoundChoice.Chord => new[] { "chord.wav" },
            SoundChoice.Ding => new[] { "ding.wav", "Windows Ding.wav" },
            SoundChoice.Notify => new[] { "notify.wav", "Windows Notify.wav", "Windows Notify System Generic.wav" },
            SoundChoice.Tada => new[] { "tada.wav" },
            _ => Array.Empty<string>(),
        };
        foreach (var name in candidates)
        {
            var p = Path.Combine(MediaDir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static IReadOnlyList<SoundChoice> AllChoices { get; } = new[]
    {
        SoundChoice.None,
        SoundChoice.Chimes,
        SoundChoice.Chord,
        SoundChoice.Ding,
        SoundChoice.Notify,
        SoundChoice.Tada,
        SoundChoice.Beep,
    };

    public static string Display(SoundChoice c)
    {
        var L = App.Current.Loc;
        return c switch
        {
            SoundChoice.None => L.T("Settings.Sound.None"),
            SoundChoice.Chimes => "Chimes",
            SoundChoice.Chord => "Chord",
            SoundChoice.Ding => "Ding",
            SoundChoice.Notify => "Notify",
            SoundChoice.Tada => "Tada",
            SoundChoice.Beep => L.T("Settings.Sound.SystemBeep"),
            _ => c.ToString(),
        };
    }
}
