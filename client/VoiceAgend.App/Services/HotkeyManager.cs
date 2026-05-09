using System.Runtime.InteropServices;

namespace VoiceAgend.App.Services;

/// <summary>
/// Globaler Hotkey via Win32 RegisterHotKey. Versteckt eine Message-Window-Schleife
/// auf einem dedizierten Thread, um WM_HOTKEY zu empfangen.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xC001;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    private Thread? _thread;
    private uint _threadId;
    private uint _modifiers;
    private uint _vk;
    private bool _registered;

    public event Action? Pressed;

    public void Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        _modifiers = modifiers;
        _vk = virtualKey;
        _thread = new Thread(MessageLoop) { IsBackground = true };
        _thread.Start();
    }

    public void Unregister()
    {
        if (_thread is null) return;
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(500);
        _thread = null;
        _threadId = 0;
        _registered = false;
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _registered = RegisterHotKey(IntPtr.Zero, HOTKEY_ID, _modifiers, _vk);
        if (!_registered) return;
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WM_HOTKEY && msg.WParam.ToInt32() == HOTKEY_ID)
                    Pressed?.Invoke();
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
        }
    }

    public void Dispose() => Unregister();
}
