using System.Runtime.InteropServices;

namespace VoiceAgend.App.Services;

/// <summary>
/// Globaler Low-Level-Maus-Hook (WH_MOUSE_LL). Feuert <see cref="Scrolled"/> nur, wenn beim
/// Mausrad-Scroll der konfigurierte Aufnahme-Hotkey-Chord gerade physisch gehalten wird —
/// dann wird das Scroll-Event geschluckt (das Fenster unter dem Cursor scrollt nicht mit).
/// Struktur (dedizierter Thread + Message-Loop + WM_QUIT) gespiegelt von <see cref="HotkeyManager"/>.
/// </summary>
public sealed class MouseWheelHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const uint WM_QUIT = 0x0012;

    // RegisterHotKey-Modifier-Flags
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    // Virtual-Key-Codes
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd; public uint Message; public IntPtr WParam; public IntPtr LParam;
        public uint Time; public int X; public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Delegat MUSS als Feld gehalten werden, sonst sammelt der GC ihn ein → Crash im Hook-Callback.
    private readonly HookProc _proc;
    private readonly Func<(uint Mods, uint Vk)> _chordProvider;
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;

    /// <summary>Scroll-Richtung bei gehaltenem Hotkey: +1 = vorwärts (Rad nach oben), -1 = rückwärts.</summary>
    public event Action<int>? Scrolled;

    public MouseWheelHook(Func<(uint Mods, uint Vk)> chordProvider)
    {
        _chordProvider = chordProvider;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(MessageLoop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null) return;
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(500);
        _thread = null;
        _threadId = 0;
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Logger.Warn("MouseWheelHook: SetWindowsHookEx failed");
            return;
        }
        try
        {
            // WH_MOUSE_LL braucht eine laufende Message-Loop auf dem installierenden Thread.
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_MOUSEWHEEL)
        {
            var (mods, vk) = _chordProvider();
            if (vk != 0 && IsChordHeld(mods, vk))
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // Delta steht im High-Word von mouseData (signed short). >0 = nach oben.
                short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                int dir = delta > 0 ? 1 : -1;
                try { Scrolled?.Invoke(dir); } catch (Exception ex) { Logger.Warn("MouseWheelHook cb: " + ex.Message); }
                return (IntPtr)1; // Event schlucken — kein Scroll an das Fenster unter dem Cursor.
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Down(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>Sind alle Modifier des Chords UND die Haupttaste gerade gedrückt?</summary>
    public static bool IsChordHeld(uint mods, uint vk)
    {
        if ((mods & MOD_CONTROL) != 0 && !Down(VK_CONTROL)) return false;
        if ((mods & MOD_SHIFT) != 0 && !Down(VK_SHIFT)) return false;
        if ((mods & MOD_ALT) != 0 && !Down(VK_MENU)) return false;
        if ((mods & MOD_WIN) != 0 && !(Down(VK_LWIN) || Down(VK_RWIN))) return false;
        return Down((int)vk);
    }

    /// <summary>Ist die angegebene Virtual-Key gerade gedrückt?</summary>
    public static bool IsKeyDown(uint vk) => vk != 0 && Down((int)vk);

    public void Dispose() => Stop();
}
