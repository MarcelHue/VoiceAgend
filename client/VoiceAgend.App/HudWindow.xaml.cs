using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
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
    /// <summary>
    /// Natürliche Pillenhöhe in DIPs (= Root.Height). Die Breite ergibt sich aus den sichtbaren
    /// Elementen; beides wird mit DPI und der Nutzer-Skalierung (HudScale) multipliziert.
    /// </summary>
    private const double BaseHeightDip = 96;

    private int _widthPx;
    private int _heightPx;
    private HudPosition _anchor = HudPosition.TopRight;
    private int _margin = 24;
    private readonly IntPtr _hwnd;

    // Zuletzt angezeigter Zustand — damit Einstellungsänderungen live neu gerendert werden können.
    private HudState _lastState = HudState.Hidden;
    private string _lastText = "";
    private bool _editMode;

    private readonly DispatcherQueueTimer _autoHideTimer;
    private readonly DispatcherQueueTimer _recTimer;
    private Storyboard? _pulseStoryboard;
    private Storyboard? _dotsStoryboard;
    private DateTime _recStart = DateTime.MinValue;
    // Verschiebe-Modus: Drag-Ende erkennen; eigene Moves (Reposition) nicht als Drag werten.
    private readonly DispatcherQueueTimer _dragEndTimer;
    private PointInt32 _lastProgrammaticPos;

    /// <summary>Im Verschiebe-Modus per Drag abgelegte Position (obere linke Ecke, physische Pixel).</summary>
    public event Action<int, int>? CustomPositionPicked;

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

        // Per-Pixel-Transparenz: durchsichtiger System-Backdrop, die Pille malt ihren
        // (halbtransparenten) Hintergrund selbst → Inhalt bleibt voll deckend.
        SystemBackdrop = new TransparentBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        SetToolWindowAndLayered(_hwnd);
        EnableDwmRoundedCorners(_hwnd);
        // Fenster-Alpha voll: die Transparenz kommt nur noch vom Hintergrund-Brush.
        // Layered bleibt trotzdem nötig — WS_EX_TRANSPARENT (Klicks gehen durch) greift
        // systemweit nur auf Layered-Fenstern.
        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            if (_editMode) return;
            StopAnimations();
            AppWindow.Hide();
            _lastState = HudState.Hidden;
        };

        _recTimer = DispatcherQueue.CreateTimer();
        _recTimer.Interval = TimeSpan.FromSeconds(1);
        _recTimer.Tick += (_, _) => UpdateRecTimer();

        // Nativer Drag (Zieh-Region im Verschiebe-Modus) meldet sich nur über Positions-
        // änderungen. Kurz nach der letzten Änderung als Custom-Position übernehmen.
        _dragEndTimer = DispatcherQueue.CreateTimer();
        _dragEndTimer.Interval = TimeSpan.FromMilliseconds(400);
        _dragEndTimer.IsRepeating = false;
        _dragEndTimer.Tick += (_, _) =>
        {
            var p = AppWindow.Position;
            CustomPositionPicked?.Invoke(p.X, p.Y);
        };
        AppWindow.Changed += (_, a) =>
        {
            if (!a.DidPositionChange || !_editMode) return;
            var p = AppWindow.Position;
            if (p.X == _lastProgrammaticPos.X && p.Y == _lastProgrammaticPos.Y) return;
            _dragEndTimer.Stop();
            _dragEndTimer.Start();
        };

        // Ändert sich die natürliche Inhaltsbreite (Elemente ein/aus, anderer Statustext),
        // ziehen Fenster und damit der Pillen-Hintergrund automatisch nach.
        Root.SizeChanged += (_, _) => UpdateWindowSize();
        UpdateWindowSize();
        AppWindow.Hide();
    }

    // ============================== GRÖSSE & POSITION ==============================

    public void ApplyPosition(HudPosition pos, int margin)
    {
        _anchor = pos;
        _margin = margin;
        Reposition();
    }

    /// <summary>
    /// Fenstergröße = gemessene Inhaltsbreite × DPI × Nutzer-Skalierung. Die Viewbox im XAML
    /// skaliert den Inhalt entsprechend, die GDI-Region schneidet die Pillenform.
    /// </summary>
    private void UpdateWindowSize()
    {
        var s = App.Current.Settings;
        Root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var widthDip = Math.Max(BaseHeightDip, Root.DesiredSize.Width);
        var dpi = GetDpiForWindow(_hwnd);
        var raster = dpi > 0 ? dpi / 96.0 : 1.0;
        var scale = Math.Clamp(s.HudScale, 50, 200) / 100.0;
        var w = (int)Math.Ceiling(widthDip * raster * scale);
        var h = (int)Math.Ceiling(BaseHeightDip * raster * scale);
        if (w != _widthPx || h != _heightPx)
        {
            _widthPx = w;
            _heightPx = h;
            AppWindow.Resize(new SizeInt32(w, h));
            ApplyRoundedRegion();
            if (_editMode) UpdateDragRegion();
        }
        Reposition();
    }

    private void Reposition()
    {
        var s = App.Current.Settings;
        int x, y;
        if (_anchor == HudPosition.Custom && s.HudCustomX is int cx && s.HudCustomY is int cy)
        {
            // Benutzerdefiniert: in den Arbeitsbereich des Monitors klemmen, auf dem der Punkt
            // liegt — falls sich Auflösung/Monitor-Anordnung seitdem geändert hat.
            var area = DisplayArea.GetFromPoint(
                new PointInt32(cx + _widthPx / 2, cy + _heightPx / 2), DisplayAreaFallback.Nearest).WorkArea;
            x = Math.Clamp(cx, area.X, Math.Max(area.X, area.X + area.Width - _widthPx));
            y = Math.Clamp(cy, area.Y, Math.Max(area.Y, area.Y + area.Height - _heightPx));
        }
        else
        {
            // Custom ohne gespeicherte Koordinaten (z. B. per Sync von einem anderen Gerät)
            // fällt auf oben rechts zurück.
            var anchor = _anchor == HudPosition.Custom ? HudPosition.TopRight : _anchor;
            var w = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            x = anchor switch
            {
                HudPosition.TopLeft or HudPosition.MiddleLeft or HudPosition.BottomLeft => w.X + _margin,
                HudPosition.TopCenter or HudPosition.MiddleCenter or HudPosition.BottomCenter => w.X + (w.Width - _widthPx) / 2,
                _ => w.X + w.Width - _widthPx - _margin,
            };
            y = anchor switch
            {
                HudPosition.TopLeft or HudPosition.TopCenter or HudPosition.TopRight => w.Y + _margin,
                HudPosition.MiddleLeft or HudPosition.MiddleCenter or HudPosition.MiddleRight => w.Y + (w.Height - _heightPx) / 2,
                _ => w.Y + w.Height - _heightPx - _margin,
            };
        }
        // Vor dem Move merken: AppWindow.Changed soll eigene Moves nicht als Drag werten.
        _lastProgrammaticPos = new PointInt32(x, y);
        AppWindow.Move(_lastProgrammaticPos);
    }

    private void ApplyRoundedRegion()
    {
        // AppWindow.Resize und SetWindowRgn nutzen beide physische Pixel.
        // Durchmesser = Höhe → perfekte Pille (Region bleibt Sicherheitsnetz, falls die
        // Per-Pixel-Transparenz auf einem System nicht greift).
        try
        {
            var rgn = CreateRoundRectRgn(0, 0, _widthPx + 1, _heightPx + 1, _heightPx, _heightPx);
            SetWindowRgn(_hwnd, rgn, true);
        }
        catch { /* GDI nicht da → DWM-Rundung als Fallback */ }
    }

    // ============================== ANZEIGEN / AUSBLENDEN ==============================

    public void Show(HudState state, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _lastState = state;
            _lastText = text;
            StopAnimations();
            ApplyState(state, text);
            UpdateWindowSize();
            AppWindow.Show();
            _autoHideTimer.Stop();
            if (!_editMode && (state is HudState.Done or HudState.Error))
                _autoHideTimer.Start();
        });
    }

    public void Hide() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_editMode) return; // im Verschiebe-Modus bleibt das HUD greifbar sichtbar
        StopAnimations();
        _autoHideTimer.Stop();
        AppWindow.Hide();
        _lastState = HudState.Hidden;
    });

    /// <summary>Einstellungsänderung (Elemente, Größe, Deckkraft) sofort auf ein sichtbares HUD anwenden.</summary>
    public void RefreshLayout() => DispatcherQueue.TryEnqueue(() =>
    {
        if (AppWindow.IsVisible && _lastState != HudState.Hidden)
        {
            StopAnimations();
            ApplyState(_lastState, _lastText);
        }
        UpdateWindowSize();
    });

    public void RefreshBackground() => RefreshLayout();

    /// <summary>
    /// Verschiebe-Modus: Klick-Durchlass aus, damit das HUD per Maus gegriffen werden kann;
    /// Akzent-Rahmen + "ziehen"-Hinweis als Erkennungszeichen.
    /// </summary>
    public void SetEditMode(bool on) => DispatcherQueue.TryEnqueue(() =>
    {
        _editMode = on;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = on ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        UpdateDragRegion();
        Root.BorderBrush = new SolidColorBrush(ThemeColor("VAAccentBrush"));
        Root.BorderThickness = new Thickness(on ? 2 : 0);
        if (_lastState != HudState.Hidden)
        {
            StopAnimations();
            ApplyState(_lastState, _lastText);
            UpdateWindowSize();
        }
    });

    /// <summary>
    /// Im Verschiebe-Modus ist die ganze Pille "Titelleiste": Windows zieht das Fenster nativ.
    /// (XAML-Pointer-Events kommen im nicht aktivierbaren Layered-Fenster nicht zuverlässig an.)
    /// </summary>
    private void UpdateDragRegion()
    {
        try
        {
            var src = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            var rects = _editMode
                ? new[] { new RectInt32(0, 0, _widthPx, _heightPx) }
                : Array.Empty<RectInt32>();
            src.SetRegionRects(NonClientRegionKind.Caption, rects);
        }
        catch (Exception ex) { Logger.Warn("HUD drag region: " + ex.Message); }
    }

    // ============================== STATE RENDERING ==============================

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyState(HudState state, string statusText)
    {
        var L = App.Current.Loc;
        var s = App.Current.Settings;
        var accent = ThemeColor("VAAccentBrush");
        var danger = ThemeColor("VADangerBrush");
        var ok = ThemeColor("VAOkBrush");
        var warn = ThemeColor("VAWarnBrush");
        var bg = ThemeColor("VABgRaisedBrush");

        // Reset aller zustandsabhängigen Inhalte
        BadgeDot.Visibility = Visibility.Collapsed;
        BadgeIcon.Visibility = Visibility.Collapsed;
        BadgeDots.Visibility = Visibility.Collapsed;
        BadgePulseRing.Opacity = 0;
        HudWave.Visibility = Visibility.Collapsed;
        HudProgress.Visibility = Visibility.Collapsed;
        KbdPill.Visibility = Visibility.Collapsed;
        RightInfo.Text = "";
        _recTimer.Stop();

        // StateValue-Default-Style (Recording überschreibt das in UpdateRecTimer)
        StateValue.FontFamily = (FontFamily)Application.Current.Resources["VAFont"];
        StateValue.FontSize = 18;
        StateValue.CharacterSpacing = 0;

        // Pillen-Hintergrund mit einstellbarer Deckkraft — nur der Hintergrund, der
        // Inhalt (Punkt, Waveform, Text) bleibt voll deckend.
        BgBrush.Color = WithOpacity(bg, s.HudBackgroundOpacity);

        var showInfo = false;
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
                _recTimer.Start();
                UpdateRecTimer();
                HudWave.Visibility = Visibility.Visible;
                HudWave.IsActive = true;
                HudWave.RefreshBrush();
                KbdPill.BorderBrush = new SolidColorBrush(ThemeColor("VABorderSoftBrush"));
                KbdText.Foreground = new SolidColorBrush(ThemeColor("VATextDimBrush"));
                KbdText.Text = s.HotkeyDisplay();
                KbdPill.Visibility = Visibility.Visible;
                RightInfo.Foreground = new SolidColorBrush(ThemeColor("VATextMuteBrush"));
                RightInfo.Text = L.T("Hud.HotkeyStop");
                StartPulseAnimation();
                showInfo = true;
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
                showInfo = true;
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
                showInfo = true;
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
                break;

            case HudState.Error:
                // Bei Sendefehlern bleibt die Aufnahme zum erneuten Senden erhalten →
                // "Fehlgeschlagen" statt "Verworfen", sonst wäre die Anzeige irreführend.
                // Erkennung über den festen Satzteil hinter {0} — "Fehler:" allein trifft
                // auch Mic-/Stop-Fehler, bei denen nichts gespeichert wurde.
                var retryTail = L.T("Status.ErrorRetryFmt").Split("{0}").LastOrDefault()?.Trim() ?? "";
                StateLabel.Text = retryTail.Length > 0 && statusText.EndsWith(retryTail)
                    ? L.T("Hud.State.Failed")
                    : L.T("Hud.State.Discarded");
                StateLabel.Foreground = new SolidColorBrush(warn);
                BadgeBg.Fill = new SolidColorBrush(warn);
                BadgeIcon.Glyph = ""; // warning
                BadgeIcon.Foreground = new SolidColorBrush(warn);
                BadgeIcon.Visibility = Visibility.Visible;
                StateValue.Text = statusText;
                RightInfo.Foreground = new SolidColorBrush(ThemeColor("VATextMuteBrush"));
                _recStart = DateTime.MinValue;
                break;

            case HudState.LanguageSwitch:
                StateLabel.Text = L.T("Hud.State.Language");
                StateLabel.Foreground = new SolidColorBrush(accent);
                BadgeBg.Fill = new SolidColorBrush(accent);
                BadgeIcon.Glyph = ""; // globe
                BadgeIcon.Foreground = new SolidColorBrush(accent);
                BadgeIcon.Visibility = Visibility.Visible;
                StateValue.Text = statusText; // Sprach-Anzeigename
                RightInfo.Foreground = new SolidColorBrush(ThemeColor("VATextMuteBrush"));
                RightInfo.Text = L.T("Hud.HotkeyStop");
                _recStart = DateTime.MinValue;
                break;
        }

        if (_editMode)
        {
            RightInfo.Foreground = new SolidColorBrush(accent);
            RightInfo.Text = L.T("Hud.DragHint");
        }

        ApplyElementVisibility(s, showInfo);
    }

    /// <summary>Nutzer-Auswahl der Elemente anwenden. Die Pille passt ihre Breite danach
    /// über Root.SizeChanged automatisch an.</summary>
    private void ApplyElementVisibility(AppSettings s, bool showInfo)
    {
        BadgeHost.Visibility = Vis(s.HudShowBadge);
        StateLabel.Visibility = Vis(s.HudShowTitle);
        StateValue.Visibility = Vis(s.HudShowValue);
        TextHost.Visibility = Vis(s.HudShowTitle || s.HudShowValue);

        if (!s.HudShowWaveform)
        {
            HudWave.Visibility = Visibility.Collapsed;
            HudProgress.Visibility = Visibility.Collapsed;
        }
        WaveHost.Visibility = Vis(HudWave.Visibility == Visibility.Visible
                                  || HudProgress.Visibility == Visibility.Visible);

        if (!s.HudShowHotkey) KbdPill.Visibility = Visibility.Collapsed;
        RightInfo.Visibility = Vis(RightInfo.Text.Length > 0 && (s.HudShowHint || _editMode));

        UpdateInfoChips(s, showInfo);
    }

    private void UpdateInfoChips(AppSettings s, bool showInfo)
    {
        var L = App.Current.Loc;
        var dim = new SolidColorBrush(ThemeColor("VATextDimBrush"));
        var border = new SolidColorBrush(ThemeColor("VABorderSoftBrush"));
        var warn = new SolidColorBrush(ThemeColor("VAWarnBrush"));

        var lang = (s.Language ?? "").Trim();
        LangChipText.Text = lang.Length == 0 ? "AUTO" : lang.ToUpperInvariant();
        MicChipText.Text = Shorten(s.MicDeviceName, 24);
        OutChipText.Text = L.T("Hud.Out." + s.OutputMode);
        OutChipIcon.Glyph = s.OutputMode switch
        {
            OutputMode.Paste => "",        // paste
            OutputMode.Type => "",         // keyboard
            OutputMode.DirectInsert => "", // lightning
            OutputMode.Notification => "", // bell
            _ => "",                       // copy (Zwischenablage)
        };
        PendingChipText.Text = L.T("Hud.Pending");

        foreach (var (chip, icon, text) in new[]
                 {
                     (LangChip, LangChipIcon, LangChipText),
                     (MicChip, MicChipIcon, MicChipText),
                     (OutChip, OutChipIcon, OutChipText),
                 })
        {
            chip.BorderBrush = border;
            icon.Foreground = dim;
            text.Foreground = dim;
        }
        PendingChip.BorderBrush = warn;
        PendingChipIcon.Foreground = warn;
        PendingChipText.Foreground = warn;

        LangChip.Visibility = Vis(showInfo && s.HudShowLanguage);
        MicChip.Visibility = Vis(showInfo && s.HudShowMic && !string.IsNullOrWhiteSpace(s.MicDeviceName));
        OutChip.Visibility = Vis(showInfo && s.HudShowOutput);
        PendingChip.Visibility = Vis(showInfo && s.HudShowPending && App.Current.Coordinator.HasPendingRetry);
        InfoHost.Visibility = Vis(LangChip.Visibility == Visibility.Visible
                                  || MicChip.Visibility == Visibility.Visible
                                  || OutChip.Visibility == Visibility.Visible
                                  || PendingChip.Visibility == Visibility.Visible);
    }

    private static string Shorten(string? text, int max)
    {
        var t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    private void UpdateRecTimer()
    {
        if (_recStart == DateTime.MinValue) return;
        var sec = (int)(DateTime.Now - _recStart).TotalSeconds;
        StateValue.Text = $"{sec / 60:D2}:{sec % 60:D2}";
        StateValue.FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"];
        StateValue.FontSize = 28;
        StateValue.CharacterSpacing = -30;
    }

    private static Color ThemeColor(string brushKey) =>
        ((SolidColorBrush)Application.Current.Resources[brushKey]).Color;

    private static Color WithOpacity(Color c, int percent)
    {
        var a = (byte)Math.Round(Math.Clamp(percent, 0, 100) * 2.55);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

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
        // Waveform-Timer anhalten (Recording schaltet ihn in ApplyState wieder ein).
        HudWave.IsActive = false;
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
    // WS_EX_TRANSPARENT + WS_EX_LAYERED = Klicks/Scrollen gehen an das Fenster darunter.
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
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
        // Das HUD ist reine Anzeige (keine Bedienelemente) → standardmäßig klick-durchlässig.
        // Nur im Verschiebe-Modus (SetEditMode) wird WS_EX_TRANSPARENT entfernt.
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    private static void EnableDwmRoundedCorners(IntPtr hwnd)
    {
        // DWM-Rundung deaktivieren — Region kümmert sich um die Pillen-Form.
        // Bei beiden aktiv käme ein doppelter Anti-Alias-Rand.
        try { var v = DWMWCP_DONOTROUND; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int)); }
        catch { }
        // Ganzen Client-Bereich zum DWM-"Glas" machen: Als rahmenloses WS_POPUP hätte das
        // Fenster sonst keinen Glas-Bereich, DWM zeichnet es undurchsichtig, und der
        // halbtransparente Pillen-Hintergrund würde nur Richtung Schwarz abgedunkelt statt
        // den Desktop durchscheinen zu lassen.
        try { var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 }; DwmExtendFrameIntoClientArea(hwnd, ref m); }
        catch { }
    }
}
