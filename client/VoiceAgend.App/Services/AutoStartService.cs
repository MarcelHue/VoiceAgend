using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VoiceAgend.App.Services;

/// <summary>
/// Auto-Start mit Windows via HKCU\…\Run-Registry-Eintrag.
/// Funktioniert für unpackaged Installs (Velopack), kein UAC nötig.
/// </summary>
public sealed class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VoiceAgend";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch (Exception ex)
            {
                Logger.Warn("AutoStart read: " + ex.Message);
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (enabled)
            {
                var path = GetLauncherPath();
                if (string.IsNullOrEmpty(path)) return;
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AutoStart write", ex);
        }
    }

    /// <summary>
    /// Findet den stabilen Launcher-Pfad. Bei Velopack-Install liegt der Forwarder
    /// im Install-Root (Parent von current/), bleibt über Updates hinweg gleich.
    /// </summary>
    private static string GetLauncherPath()
    {
        try
        {
            // current/-Ordner (BaseDirectory) → Parent ist Install-Root
            var current = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var parent = Directory.GetParent(current);
            if (parent != null)
            {
                var fwd = Path.Combine(parent.FullName, "VoiceAgend.App.exe");
                if (File.Exists(fwd)) return fwd;
            }
        }
        catch { /* fallback unten */ }

        return Process.GetCurrentProcess().MainModule?.FileName ?? "";
    }
}
