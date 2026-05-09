using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using VoiceAgend.App.Models;

namespace VoiceAgend.App.Services;

public sealed class OutputService
{
    private DispatcherQueue? _ui;

    public void AttachUiThread(DispatcherQueue ui) => _ui = ui;

    private void OnUi(Action a)
    {
        if (_ui is null) { a(); return; }
        if (_ui.HasThreadAccess) { a(); return; }
        _ui.TryEnqueue(() => a());
    }

    public void Dispatch(OutputMode mode, string text, bool showToast)
    {
        switch (mode)
        {
            case OutputMode.Clipboard:
                CopyToClipboard(text);
                if (showToast) Toast("Transkript in Zwischenablage", text);
                break;
            case OutputMode.Type:
                CopyToClipboard(text); // fallback parallel
                TypeText(text);
                break;
            case OutputMode.Notification:
                Toast("Transkript", text);
                break;
        }
    }

    public void Toast(string title, string body) => OnUi(() =>
    {
        try
        {
            var n = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(n);
        }
        catch (Exception ex)
        {
            Logger.Warn("Toast failed: " + ex.Message);
        }
    });

    public void CopyToClipboard(string text) => OnUi(() =>
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        catch (Exception ex)
        {
            Logger.Error("Clipboard.SetContent failed", ex);
            CopyToClipboardWin32(text);
        }
    });

    // Win32-Fallback ohne WinRT-Apartment-Anforderungen
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static void CopyToClipboardWin32(string text)
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

    // ---- SendInput für "direkt tippen" ----

    // Auf x64 muss INPUT exakt 40 Byte (4+4 Pad+32 Union) groß sein,
    // sonst lehnt SendInput die Daten still ab.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            // CRLF/LF → Enter (VK_RETURN), Unicode-Pfad mag das nicht
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                inputs.Add(MakeVk(0x0D, false));
                inputs.Add(MakeVk(0x0D, true));
                continue;
            }
            inputs.Add(MakeUnicode(ch, false));
            inputs.Add(MakeUnicode(ch, true));
        }
        var arr = inputs.ToArray();
        var size = Marshal.SizeOf<INPUT>();
        var sent = SendInput((uint)arr.Length, arr, size);
        if (sent != arr.Length)
            Logger.Warn($"SendInput sent {sent}/{arr.Length}; lastErr={Marshal.GetLastWin32Error()}; size={size}");
    }

    private static INPUT MakeUnicode(char c, bool keyUp)
    {
        var flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        // Surrogate-Pairs müssen als EXTENDEDKEY markiert werden
        if ((c & 0xFF00) == 0xE000) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT { WVk = 0, WScan = c, DwFlags = flags }
            }
        };
    }

    private static INPUT MakeVk(ushort vk, bool keyUp) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                WVk = vk,
                WScan = 0,
                DwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
            }
        }
    };
}
