using System.Runtime.InteropServices;

namespace VoiceAgend.App.Services;

/// <summary>
/// Schreibt unbehandelte Exceptions ins Log und legt sie zusätzlich in die
/// Zwischenablage, damit sie sofort an den User schickbar sind.
/// </summary>
public static class CrashHandler
{
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Handle("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    public static void Handle(string source, Exception? ex)
    {
        if (ex is null) return;
        var text = $"VoiceAgend Crash @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                   $"Source: {source}\n" +
                   $"{ex.GetType().FullName}: {ex.Message}\n" +
                   $"{ex}\n" +
                   $"Log: {Logger.FilePath}";
        Logger.Error(source, ex);
        TryCopyToClipboard(text);
    }

    // Direkter Win32-Clipboard-Call (UI-Thread egal, kein WinRT-Apartment).
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static void TryCopyToClipboard(string text)
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return;
            try
            {
                EmptyClipboard();
                var bytes = (text.Length + 1) * 2;
                var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) return;
                var pMem = GlobalLock(hMem);
                try { Marshal.Copy(text.ToCharArray(), 0, pMem, text.Length); }
                finally { GlobalUnlock(hMem); }
                SetClipboardData(CF_UNICODETEXT, hMem);
            }
            finally { CloseClipboard(); }
        }
        catch { /* Crash-Handler darf nie selbst crashen */ }
    }
}
