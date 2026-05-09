using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using VoiceAgend.App.Models;
using VoiceAgend.App.Services;
using WinRT.Interop;

namespace VoiceAgend.App;

public sealed partial class HudWindow : Window
{
    private const int MinWidth = 220;
    private const int MaxWidth = 900;
    private const int Height = 72;
    private const int HorizontalPadding = 90;
    private int _currentWidth = MinWidth;

    private HudPosition _anchor = HudPosition.TopRight;
    private int _margin = 24;

    private readonly DispatcherQueueTimer _autoHideTimer;

    public HudWindow()
    {
        InitializeComponent();
        Title = "VoiceAgend HUD";

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        var hwnd = WindowNative.GetWindowHandle(this);
        SetToolWindowAndLayered(hwnd);
        EnableDwmRoundedCorners(hwnd);
        // Per-Window-Alpha 220/255 ≈ 86% deckend, 14% transparent
        SetLayeredWindowAttributes(hwnd, 0, 220, LWA_ALPHA);

        AppWindow.Resize(new SizeInt32(MinWidth, Height));
        _currentWidth = MinWidth;
        AppWindow.Hide();

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            AppWindow.Hide();
        };
    }

    public void ApplyPosition(HudPosition pos, int margin)
    {
        _anchor = pos;
        _margin = margin;
        Reposition();
    }

    private void Reposition()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var w = area.WorkArea;
        var widthNow = _currentWidth;

        // Position berechnet relativ zum gewählten Anker:
        // - Links → Fenster wächst nach rechts (X = linker Rand + margin)
        // - Rechts → Fenster wächst nach links (X = rechter Rand - width - margin)
        // - Mitte → Fenster bleibt zentriert
        int x = _anchor switch
        {
            HudPosition.TopLeft or HudPosition.MiddleLeft or HudPosition.BottomLeft
                => w.X + _margin,
            HudPosition.TopCenter or HudPosition.MiddleCenter or HudPosition.BottomCenter
                => w.X + (w.Width - widthNow) / 2,
            _ // Right-Varianten
                => w.X + w.Width - widthNow - _margin,
        };
        int y = _anchor switch
        {
            HudPosition.TopLeft or HudPosition.TopCenter or HudPosition.TopRight
                => w.Y + _margin,
            HudPosition.MiddleLeft or HudPosition.MiddleCenter or HudPosition.MiddleRight
                => w.Y + (w.Height - Height) / 2,
            _ // Bottom-Varianten
                => w.Y + w.Height - Height - _margin,
        };
        AppWindow.Move(new PointInt32(x, y));
    }

    private void ResizeToText(string text)
    {
        // 14pt Segoe UI SemiBold: durchschnittliche Glyph-Breite ~ 8.8 px,
        // Großbuchstaben/Umlaute liegen bei 11-13 px. Wir rechnen großzügig (12) +
        // 90 px Reserve (Padding+Dot+Sicherheit), damit auch "Transkription" sauber reinpasst.
        var estimated = (int)Math.Ceiling(text.Length * 12.0);
        var target = Math.Clamp(estimated + HorizontalPadding, MinWidth, MaxWidth);
        if (target == _currentWidth) return;
        _currentWidth = target;
        AppWindow.Resize(new SizeInt32(target, Height));
        Reposition(); // nach jeder Größenänderung neu am Anker ausrichten
    }

    public void Show(HudState state, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            (Color bg, Color dot) = ColorsFor(state);
            BgBrush.Color = bg;
            DotBrush.Color = dot;
            // Erst Größe anpassen (Layout-Kontext freischalten), dann Text setzen
            ResizeToText(text);
            Label.Text = text;
            AppWindow.Show();
            _autoHideTimer.Stop();
            if (state is HudState.Done or HudState.Error)
                _autoHideTimer.Start();
        });
    }

    public void Hide()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _autoHideTimer.Stop();
            AppWindow.Hide();
        });
    }

    // Volle Sättigung — die Transparenz kommt jetzt vom Layered-Window-Style
    private static (Color bg, Color dot) ColorsFor(HudState s) => s switch
    {
        HudState.Recording => (FromHex(0xFF, 0xC0, 0x29, 0x29), FromHex(0xFF, 0xFF, 0x55, 0x55)),
        HudState.Sending or HudState.Processing => (FromHex(0xFF, 0x1E, 0x44, 0x88), FromHex(0xFF, 0x6E, 0xA8, 0xFF)),
        HudState.Done => (FromHex(0xFF, 0x1E, 0x66, 0x33), FromHex(0xFF, 0x66, 0xDD, 0x88)),
        HudState.Error => (FromHex(0xFF, 0x55, 0x55, 0x55), FromHex(0xFF, 0xFF, 0x88, 0x33)),
        _ => (FromHex(0xFF, 0x20, 0x20, 0x20), FromHex(0xFF, 0x88, 0x88, 0x88)),
    };

    private static Color FromHex(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    // ----- Window-Styles -----
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_BORDER = 0x00800000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static void SetToolWindowAndLayered(IntPtr hwnd)
    {
        // Frame-Bits ganz aus dem Style entfernen
        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_CAPTION | WS_DLGFRAME | WS_THICKFRAME | WS_SYSMENU);
        style |= WS_POPUP;
        SetWindowLong(hwnd, GWL_STYLE, style);

        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);

        // Frame-Cache von Windows aktualisieren
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    // DWM-Rundung der Fensterecken (Windows 11), löst weiße/schwarze Ecken auf
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static void EnableDwmRoundedCorners(IntPtr hwnd)
    {
        try
        {
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* unter Windows 10 nicht verfügbar — egal */ }
    }
}
