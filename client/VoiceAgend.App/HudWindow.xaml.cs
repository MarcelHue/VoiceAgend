using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using VoiceAgend.App.Models;
using VoiceAgend.App.Services;
using WinRT.Interop;

namespace VoiceAgend.App;

public sealed partial class HudWindow : Window
{
    private const int MinWidth = 380;
    private const int MaxWidth = 760;
    private const int Height = 96;

    private int _currentWidth = MinWidth;
    private HudPosition _anchor = HudPosition.TopRight;
    private int _margin = 24;
    private IntPtr _hwnd;

    private readonly DispatcherQueueTimer _autoHideTimer;
    private Storyboard? _pulseStoryboard;
    private Storyboard? _dotsStoryboard;
    private DateTime _recStart = DateTime.MinValue;
    private DispatcherQueueTimer? _recTimer;

    public HudWindow()
    {
        InitializeComponent();
        Title = "VoiceAgend HUD";

        if (Content is FrameworkElement root)
            Themes.ThemeManager.RegisterRoot(root);

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        _hwnd = WindowNative.GetWindowHandle(this);
        SetToolWindowAndLayered(_hwnd);
        EnableDwmRoundedCorners(_hwnd);
        SetLayeredWindowAttributes(_hwnd, 0, 232, LWA_ALPHA);

        AppWindow.Resize(new SizeInt32(MinWidth, Height));
        _currentWidth = MinWidth;
        ApplyRoundedRegion();
        AppWindow.Hide();

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            AppWindow.Hide();
        };

        _recTimer = DispatcherQueue.CreateTimer();
        _recTimer.Interval = TimeSpan.FromSeconds(1);
        _recTimer.Tick += (_, _) => UpdateRecTimer();
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
        int x = _anchor switch
        {
            HudPosition.TopLeft or HudPosition.MiddleLeft or HudPosition.BottomLeft => w.X + _margin,
            HudPosition.TopCenter or HudPosition.MiddleCenter or HudPosition.BottomCenter => w.X + (w.Width - widthNow) / 2,
            _ => w.X + w.Width - widthNow - _margin,
        };
        int y = _anchor switch
        {
            HudPosition.TopLeft or HudPosition.TopCenter or HudPosition.TopRight => w.Y + _margin,
            HudPosition.MiddleLeft or HudPosition.MiddleCenter or HudPosition.MiddleRight => w.Y + (w.Height - Height) / 2,
            _ => w.Y + w.Height - Height - _margin,
        };
        AppWindow.Move(new PointInt32(x, y));
    }

    private void Resize(int targetWidth)
    {
        targetWidth = Math.Clamp(targetWidth, MinWidth, MaxWidth);
        if (targetWidth == _currentWidth) return;
        _currentWidth = targetWidth;
        AppWindow.Resize(new SizeInt32(targetWidth, Height));
        ApplyRoundedRegion();
        Reposition();
    }

    private void ApplyRoundedRegion()
    {
        // AppWindow.Resize und SetWindowRgn nutzen beide raw physical pixels —
        // KEINE DPI-Skalierung notwendig. Durchmesser = Height für perfekte Pille.
        try
        {
            var rgn = CreateRoundRectRgn(0, 0, _currentWidth + 1, Height + 1, Height, Height);
            SetWindowRgn(_hwnd, rgn, true);
        }
        catch { /* GDI nicht da → DWM-Rundung als Fallback */ }
    }

    public void Show(HudState state, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StopAnimations();
            ApplyState(state, text);
            AppWindow.Show();
            _autoHideTimer.Stop();
            if (state is HudState.Done or HudState.Error)
                _autoHideTimer.Start();
        });
    }

    public void Hide() => DispatcherQueue.TryEnqueue(() =>
    {
        StopAnimations();
        _autoHideTimer.Stop();
        AppWindow.Hide();
    });

    // ============================== STATE RENDERING ==============================

    private void ApplyState(HudState state, string statusText)
    {
        var L = App.Current.Loc;
        var accent = ThemeColor("VAAccentBrush");
        var danger = ThemeColor("VADangerBrush");
        var ok = ThemeColor("VAOkBrush");
        var warn = ThemeColor("VAWarnBrush");
        var bg = ThemeColor("VABgRaisedBrush");

        // Reset alle Badge-Inhalte
        BadgeDot.Visibility = Visibility.Collapsed;
        BadgeIcon.Visibility = Visibility.Collapsed;
        BadgeDots.Visibility = Visibility.Collapsed;
        BadgePulseRing.Opacity = 0;
        HudWave.Visibility = Visibility.Collapsed;
        HudProgress.Visibility = Visibility.Collapsed;
        KbdPill.Visibility = Visibility.Collapsed;
        RightInfo.Text = "";
        _recTimer?.Stop();

        // StateValue-Default-Style (Recording überschreibt das in UpdateRecTimer)
        StateValue.FontFamily = (FontFamily)Application.Current.Resources["VAFont"];
        StateValue.FontSize = 18;
        StateValue.CharacterSpacing = 0;

        // Window-Hintergrund einheitlich dunkel
        BgBrush.Color = bg;

        switch (state)
        {
            case HudState.Recording:
                StateLabel.Text = L.T("Hud.State.Recording");
                StateLabel.Foreground = new SolidColorBrush(danger);
                BadgeBg.Fill = new SolidColorBrush(danger);
                BadgeDot.Fill = new SolidColorBrush(danger);
                BadgeDot.Visibility = Visibility.Visible;
                BadgePulseRing.Fill = new SolidColorBrush(danger);
                if (_recStart == DateTime.MinValue) _recStart = DateTime.Now;
                _recTimer?.Start();
                UpdateRecTimer();
                HudWave.Visibility = Visibility.Visible;
                HudWave.IsActive = true;
                HudWave.RefreshBrush();
                KbdPill.BorderBrush = new SolidColorBrush(ThemeColor("VABorderSoftBrush"));
                KbdText.Foreground = new SolidColorBrush(ThemeColor("VATextDimBrush"));
                KbdText.Text = App.Current.Settings.HotkeyDisplay();
                KbdPill.Visibility = Visibility.Visible;
                RightInfo.Foreground = new SolidColorBrush(ThemeColor("VATextMuteBrush"));
                RightInfo.Text = L.T("Hud.HotkeyStop");
                StartPulseAnimation();
                Resize(620);
                break;

            case HudState.Sending:
                StateLabel.Text = L.T("Hud.State.Sending");
                StateLabel.Foreground = new SolidColorBrush(accent);
                BadgeBg.Fill = new SolidColorBrush(accent);
                BadgeIcon.Glyph = ""; // upload
                BadgeIcon.Foreground = new SolidColorBrush(accent);
                BadgeIcon.Visibility = Visibility.Visible;
                StateValue.Text = statusText;
                HudProgress.Visibility = Visibility.Visible;
                RightInfo.Foreground = new SolidColorBrush(ThemeColor("VATextDimBrush"));
                RightInfo.Text = "…";
                _recStart = DateTime.MinValue;
                Resize(560);
                break;

            case HudState.Processing:
                StateLabel.Text = L.T("Hud.State.Transcribing");
                StateLabel.Foreground = new SolidColorBrush(accent);
                BadgeBg.Fill = new SolidColorBrush(accent);
                BadgeDots.Visibility = Visibility.Visible;
                var bdotBrush = new SolidColorBrush(accent);
                Dot1.Fill = bdotBrush; Dot2.Fill = bdotBrush; Dot3.Fill = bdotBrush;
                StateValue.Text = L.T("Hud.WaitingShort");
                RightInfo.Foreground = new SolidColorBrush(ThemeColor("VATextMuteBrush"));
                StartDotsAnimation();
                _recStart = DateTime.MinValue;
                Resize(480);
                break;

            case HudState.Done:
                StateLabel.Text = L.T("Hud.State.Done");
                StateLabel.Foreground = new SolidColorBrush(ok);
                BadgeBg.Fill = new SolidColorBrush(ok);
                BadgeIcon.Glyph = ""; // check
                BadgeIcon.Foreground = new SolidColorBrush(ok);
                BadgeIcon.Visibility = Visibility.Visible;
                StateValue.Text = L.T("Hud.Done.Output");
                RightInfo.Foreground = new SolidColorBrush(ThemeColor("VATextMuteBrush"));
                _recStart = DateTime.MinValue;
                Resize(440);
                break;

            case HudState.Error:
                StateLabel.Text = L.T("Hud.State.Discarded");
                StateLabel.Foreground = new SolidColorBrush(warn);
                BadgeBg.Fill = new SolidColorBrush(warn);
                BadgeIcon.Glyph = ""; // warning
                BadgeIcon.Foreground = new SolidColorBrush(warn);
                BadgeIcon.Visibility = Visibility.Visible;
                StateValue.Text = statusText;
                RightInfo.Foreground = new SolidColorBrush(ThemeColor("VATextMuteBrush"));
                _recStart = DateTime.MinValue;
                Resize(460);
                break;
        }
    }

    private void UpdateRecTimer()
    {
        if (_recStart == DateTime.MinValue) return;
        var s = (int)(DateTime.Now - _recStart).TotalSeconds;
        StateValue.Text = $"{s / 60:D2}:{s % 60:D2}";
        StateValue.FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"];
        StateValue.FontSize = 28;
        StateValue.CharacterSpacing = -30;
    }

    private static Color ThemeColor(string brushKey) =>
        ((SolidColorBrush)Application.Current.Resources[brushKey]).Color;

    // ============================== ANIMATIONS ==============================

    private void StartPulseAnimation()
    {
        StopAnimations();
        var sb = new Storyboard();

        // Pulsierender Ring: Scale 1 → 1.8, Opacity 0.45 → 0
        var scaleX = new DoubleAnimation { From = 1.0, To = 1.8, Duration = TimeSpan.FromSeconds(1.4), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(scaleX, BadgePulseScale);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        var scaleY = new DoubleAnimation { From = 1.0, To = 1.8, Duration = TimeSpan.FromSeconds(1.4), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(scaleY, BadgePulseScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        var ringOpacity = new DoubleAnimation { From = 0.45, To = 0, Duration = TimeSpan.FromSeconds(1.4), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(ringOpacity, BadgePulseRing);
        Storyboard.SetTargetProperty(ringOpacity, "Opacity");

        // Dot opacity flicker
        var dotOpacity = new DoubleAnimation
        {
            From = 1, To = 0.5, Duration = TimeSpan.FromMilliseconds(600),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(dotOpacity, BadgeDot);
        Storyboard.SetTargetProperty(dotOpacity, "Opacity");

        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Children.Add(ringOpacity);
        sb.Children.Add(dotOpacity);
        sb.Begin();
        _pulseStoryboard = sb;
    }

    private void StartDotsAnimation()
    {
        StopAnimations();
        var sb = new Storyboard();

        Add(Dot1T, 0);
        Add(Dot2T, 0.15);
        Add(Dot3T, 0.30);
        sb.Begin();
        _dotsStoryboard = sb;

        void Add(TranslateTransform tt, double delayS)
        {
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delayS) };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(480), Value = -4 });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1200), Value = 0 });
            Storyboard.SetTarget(anim, tt);
            Storyboard.SetTargetProperty(anim, "Y");
            sb.Children.Add(anim);
        }
    }

    private void StopAnimations()
    {
        _pulseStoryboard?.Stop();
        _dotsStoryboard?.Stop();
        _pulseStoryboard = null;
        _dotsStoryboard = null;
        BadgePulseScale.ScaleX = 1; BadgePulseScale.ScaleY = 1;
        BadgePulseRing.Opacity = 0;
        BadgeDot.Opacity = 1;
        Dot1T.Y = 0; Dot2T.Y = 0; Dot3T.Y = 0;
    }

    // ============================== WIN32 ==============================

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    private static void SetToolWindowAndLayered(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_CAPTION | WS_DLGFRAME | WS_THICKFRAME | WS_SYSMENU);
        style |= WS_POPUP;
        SetWindowLong(hwnd, GWL_STYLE, style);
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private static void EnableDwmRoundedCorners(IntPtr hwnd)
    {
        // DWM-Rundung deaktivieren — Region kümmert sich um die Pillen-Form.
        // Bei beiden aktiv käme ein doppelter Anti-Alias-Rand.
        try { var v = DWMWCP_DONOTROUND; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int)); }
        catch { }
    }
}
