using System.IO;

namespace VoiceAgend.App.Services;

public static class Logger
{
    private static readonly object Sync = new();
    public static string FilePath { get; }

    static Logger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceAgend");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "app.log");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null)
    {
        Write("ERROR", ex is null ? msg : $"{msg}\n{ex}");
    }

    private static void Write(string level, string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {msg}";
            lock (Sync) File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        catch { /* Logging darf nie crashen */ }
    }
}
