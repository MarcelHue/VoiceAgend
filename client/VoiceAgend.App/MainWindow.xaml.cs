using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using System.Windows.Input;
using Windows.System;
using VoiceAgend.App.Models;
using VoiceAgend.App.Services;


namespace VoiceAgend.App;

public sealed partial class MainWindow : Window
{
    private bool _recordingHotkey;

    public ICommand ShowWindowCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand QuitCommand { get; }

    private bool _suppressAutoSave;
    private bool _quitting;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _recTimer;
    private DateTime _recStartedAt = DateTime.MinValue;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _geometrySaveTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hfSearchDebounce;
    private CancellationTokenSource? _hfSearchCts;

    public MainWindow()
    {
        InitializeComponent();
        App.Current.Loc.LanguageChanged += () => DispatcherQueue.TryEnqueue(ApplyLocalization);
        Themes.ThemeManager.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            // Re-apply theme + Localization (manche Status-Pills nutzen Themes)
            ApplyLocalization();
        });

        // Custom Title-Bar aktivieren
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Window-Content beim ThemeManager registrieren, damit Light/Dark live umschaltet
        if (Content is FrameworkElement root)
            Themes.ThemeManager.RegisterRoot(root);

        // Initial-Geometrie: gespeicherte Größe/Position oder DPI-skalierte Min-Größe
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ApplyInitialGeometry(hwnd);

        // Debounced Auto-Save bei Größen-/Positions-Änderungen
        _geometrySaveTimer = DispatcherQueue.CreateTimer();
        _geometrySaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _geometrySaveTimer.Tick += (_, _) =>
        {
            _geometrySaveTimer!.Stop();
            PersistWindowGeometry();
        };

        // Mindest-Fenstergröße erzwingen (WinUI 3 hat keine native MinSize).
        // Wir vergleichen in EFFEKTIVEN px (DPI-normalisiert), damit die Min-Größe
        // optisch auf jedem Monitor identisch wirkt — egal ob 100% oder 150% Scaling.
        AppWindow.Changed += (sender, args) =>
        {
            if (!args.DidSizeChange && !args.DidPositionChange) return;
            var (minWPx, minHPx) = ComputeMinSize(hwnd);

            if (args.DidSizeChange)
            {
                var size = sender.Size;
                if (size.Width < minWPx || size.Height < minHPx)
                {
                    sender.Resize(new Windows.Graphics.SizeInt32(
                        Math.Max(minWPx, size.Width),
                        Math.Max(minHPx, size.Height)));
                    return; // Resize feuert wieder, das speichern wir dann
                }
            }
            // Debounce: jeden Change verlängert den Timer
            _geometrySaveTimer?.Stop();
            _geometrySaveTimer?.Start();
        };
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); }
        catch (Exception ex) { Logger.Warn("SetIcon: " + ex.Message); }
        Activated += (_, _) =>
        {
            _suppressAutoSave = true;
            try { LoadIntoUi(); }
            finally { _suppressAutoSave = false; }
        };
        Closed += OnWindowClosed;

        App.Current.Coordinator.StatusChanged += s =>
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusText.Text = s;
                HomeStatus.Text = s;
            });
        App.Current.Coordinator.StateChanged += () =>
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTrayState();
                UpdateHeroState();
            });
        App.Current.Coordinator.TranscriptReceived += t =>
            DispatcherQueue.TryEnqueue(() =>
            {
                TranscriptBox.Text = t;
                UpdateTranscriptStats();
                RefreshHistory();
            });
        App.Current.History.Changed += () =>
            DispatcherQueue.TryEnqueue(RefreshHistory);

        // Timer für die "00:12"-Anzeige im Recording-State
        _recTimer = DispatcherQueue.CreateTimer();
        _recTimer.Interval = TimeSpan.FromSeconds(1);
        _recTimer.Tick += (_, _) => UpdateHeroTimer();
        App.Current.Updates.StatusChanged += st =>
            DispatcherQueue.TryEnqueue(() => ShowUpdateStatus(st));

        // Wenn ein anderes Gerät die Settings gepusht hat und wir sie beim Startup-Sync
        // gezogen haben, müssen Theme, Locale und alle UI-Felder neu geladen werden.
        App.Current.ProfileSync.RemoteSettingsApplied += () =>
            DispatcherQueue.TryEnqueue(() =>
            {
                Themes.ThemeManager.Apply(App.Current.Settings.Theme);
                App.Current.Loc.Load(App.Current.Settings.UiLanguage);
                App.Current.ApplyHotkey();
                _suppressAutoSave = true;
                try { LoadIntoUi(); }
                finally { _suppressAutoSave = false; }
            });

        ShowWindowCommand = new RelayCommand(_ => Activate());
        ToggleCommand = new RelayCommand(async _ => await App.Current.Coordinator.ToggleAsync());
        QuitCommand = new RelayCommand(_ => DoQuit());

        // Capture key presses on the window when in hotkey-record mode
        if (Content is FrameworkElement fe)
            fe.KeyDown += OnRootKeyDown;
    }

    private void LoadIntoUi()
    {
        // _suppressAutoSave wird vom Activated-Handler aufrechterhalten —
        // hier NICHT auf false setzen, sonst feuern Initial-SelectionChanged-Events
        // ein AutoSave, das die gerade gelesenen Settings mit halb-geladenen UI-Werten
        // überschreibt (HUD/Sounds verschwinden bei jedem App-Neustart).
        HotkeyEnabledToggle.IsOn = App.Current.Settings.HotkeyEnabled;
        SetComboByTag(HomeLanguageCombo, App.Current.Settings.Language ?? "");
        SetComboByTag(HomeOutputModeCombo, App.Current.Settings.OutputMode.ToString());
        ApplyLocalization();
        UpdateHeroState();
        RefreshHistory();
        UpdateTranscriptStats();

        // Initial: Home-View aktiv
        if (Nav.SelectedItem == null)
            Nav.SelectedItem = Nav.MenuItems[0];

        // Wenn nach einem Update zum ersten Mal gestartet: WhatsNew-Seite aufzwingen
        MaybeAutoShowWhatsNew();

        // Prompt-Vorschau auf der Home-Seite vorab füllen — sonst zeigt sie bis zum
        // ersten Profil-Tab-Besuch nur den "noch nichts gesetzt"-Hinweis.
        _ = PrefetchPromptPreviewAsync();

        // Letztes Transkript einblenden, falls vorhanden
        if (!string.IsNullOrEmpty(App.Current.Coordinator.LastTranscript))
            TranscriptBox.Text = App.Current.Coordinator.LastTranscript;

        var s = App.Current.Settings;
        ServerUrlBox.Text = s.ServerUrl;
        ApiKeyBox.Password = s.ApiKey;
        SetLanguageSelection(s.Language);

        MicCombo.Items.Clear();
        MicCombo.Items.Add(new MicEntry(-1, "Standard (Windows-Default)"));
        foreach (var d in AudioCaptureService.ListDevices())
            MicCombo.Items.Add(new MicEntry(d.Number, d.Name));
        MicCombo.SelectedIndex = MicCombo.Items.Cast<MicEntry>().ToList()
            .FindIndex(e => e.Number == s.MicDeviceNumber);
        if (MicCombo.SelectedIndex < 0) MicCombo.SelectedIndex = 0;

        HotkeyDisplay.Text = s.HotkeyDisplay();

        foreach (ComboBoxItem it in OutputModeCombo.Items)
        {
            if ((string)it.Tag == s.OutputMode.ToString())
            {
                OutputModeCombo.SelectedItem = it;
                break;
            }
        }
        if (OutputModeCombo.SelectedItem == null) OutputModeCombo.SelectedIndex = 0;

        var cps = Math.Clamp(s.TypingSpeedCps, 30, 2000);
        ConfigureTypingSpeedSlider(TypingSpeedSlider, cps);
        ConfigureTypingSpeedSlider(HomeTypingSpeedSlider, cps);
        UpdateTypingSpeedValueLabels(cps);
        UpdateTypingSpeedVisibility();

        ToastToggle.IsOn = s.ShowToastOnResult;

        // Sound-Combos befüllen + Auswahl setzen
        InitSoundCombo(SoundStartCombo, s.SoundOnStart);
        InitSoundCombo(SoundStopCombo, s.SoundOnStop);
        InitSoundCombo(SoundDoneCombo, s.SoundOnDone);
        InitSoundCombo(SoundErrorCombo, s.SoundOnError);
        VolumeSlider.Value = s.SoundVolume;
        VolumeLabel.Text = App.Current.Loc.T("Settings.Volume");
        VolumeValueLabel.Text = $"{s.SoundVolume}%";

        AutoStartToggle.IsOn = App.Current.AutoStart.IsEnabled;
        HudEnabledToggle.IsOn = s.HudEnabled;
        foreach (ComboBoxItem it in HudPositionCombo.Items)
        {
            if ((string)it.Tag == s.HudPosition.ToString())
            {
                HudPositionCombo.SelectedItem = it;
                break;
            }
        }
        if (HudPositionCombo.SelectedItem == null) HudPositionCombo.SelectedIndex = 2;
    }

    private void OnHudPreview(object sender, RoutedEventArgs e)
    {
        AutoSave();
        App.Current.Hud?.Show(HudState.Recording, "HUD-Vorschau");
    }

    private void OnAutoStartToggle(object sender, RoutedEventArgs e)
    {
        var enable = AutoStartToggle.IsOn;
        App.Current.AutoStart.SetEnabled(enable);
        StatusText.Text = App.Current.Loc.T(enable ? "Settings.AutoStart.Enabled" : "Settings.AutoStart.Disabled");
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string;
        HomeView.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        ProfileView.Visibility = tag == "profile" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        WhatsNewView.Visibility = tag == "whatsnew" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "profile") _ = LoadProfileAsync();
        if (tag == "settings") ApplySettingsTab(_activeSettingsTab);
        if (tag == "whatsnew") _ = LoadWhatsNewAsync();
        if (tag == "about") RenderAbout();
    }

    private async Task PrefetchPromptPreviewAsync()
    {
        var s = App.Current.Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey) || string.IsNullOrWhiteSpace(s.ServerUrl)) return;
        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            var p = await App.Current.ServerApi.GetProfileAsync(baseUrl, s.ApiKey);
            var promptDe = p.PromptDe ?? "";
            var promptEn = p.PromptEn ?? "";
            // Legacy: ist noch nichts in DE/EN gepflegt, aber Legacy-Prompt vorhanden → als DE behandeln
            if (string.IsNullOrWhiteSpace(promptDe) && string.IsNullOrWhiteSpace(promptEn))
                promptDe = p.Prompt ?? "";
            DispatcherQueue.TryEnqueue(() => UpdatePromptPreviewForCurrentLanguage(promptDe, promptEn));
        }
        catch (Exception ex) { Logger.Warn("PrefetchPromptPreview: " + ex.Message); }
    }

    /// <summary>
    /// Wählt den Prompt entsprechend der aktuell eingestellten Spracherkennungs-Sprache.
    /// Bei Auto-Modus wird kein Prompt gesendet (deshalb auch keine Vorschau), weil ein
    /// Prompt sonst Whispers Sprachdetektion festnagelt.
    /// </summary>
    private void UpdatePromptPreviewForCurrentLanguage(string promptDe, string promptEn)
    {
        var lang = (App.Current.Settings.Language ?? "").ToLowerInvariant();
        string? active = lang switch
        {
            "de" => promptDe,
            "en" => promptEn,
            _ => null, // Auto-Modus: kein Prompt
        };
        UpdatePromptPreview(active);
    }

    /// <summary>
    /// Aktualisiert die Prompt-Vorschau auf der Home-Seite. Leerer/null-Prompt zeigt den
    /// "noch nichts gesetzt"-Hinweis, sonst den echten Prompt-Text.
    /// </summary>
    private void UpdatePromptPreview(string? prompt)
    {
        var L = App.Current.Loc;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            PromptPreviewText.Text = L.T("Home.Prompt.Empty");
            PromptPreviewText.FontStyle = Windows.UI.Text.FontStyle.Italic;
            PromptPreviewText.Foreground = (Brush)Application.Current.Resources["VATextMuteBrush"];
        }
        else
        {
            PromptPreviewText.Text = prompt!.Trim();
            PromptPreviewText.FontStyle = Windows.UI.Text.FontStyle.Normal;
            PromptPreviewText.Foreground = (Brush)Application.Current.Resources["VATextDimBrush"];
        }
    }

    // ============================= WHAT'S NEW =============================

    private bool _whatsNewLoaded;
    private bool _whatsNewAutoShown;

    /// <summary>
    /// Wenn die aktuelle App-Version ≠ LastSeenVersion → User hat geupdated,
    /// also automatisch auf die "What's New"-Seite springen und LastSeenVersion bumpen.
    /// </summary>
    private void MaybeAutoShowWhatsNew()
    {
        if (_whatsNewAutoShown) return;
        var current = WhatsNewService.CurrentVersion();
        var last = App.Current.Settings.LastSeenVersion ?? "";

        // Erster Start überhaupt (kein LastSeenVersion gespeichert) → kein WhatsNew
        // aufzwingen; nur bei einem echten Upgrade switchen.
        if (string.IsNullOrEmpty(last))
        {
            App.Current.Settings.LastSeenVersion = current;
            App.Current.SaveSettings();
            _whatsNewAutoShown = true;
            return;
        }

        if (!string.Equals(last, current, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Info($"WhatsNew: version changed {last} → {current}, switching to WhatsNew page");
            App.Current.Settings.LastSeenVersion = current;
            App.Current.SaveSettings();
            _whatsNewAutoShown = true;
            try
            {
                foreach (var mi in Nav.MenuItems)
                {
                    if (mi is NavigationViewItem nvi && (nvi.Tag as string) == "whatsnew")
                    {
                        Nav.SelectedItem = nvi;
                        break;
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("WhatsNew auto-switch: " + ex.Message); }
        }
        else
        {
            _whatsNewAutoShown = true;
        }
    }

    private async Task LoadWhatsNewAsync()
    {
        if (_whatsNewLoaded) return;
        _whatsNewLoaded = true;
        var L = App.Current.Loc;

        WhatsNewSpinner.IsActive = true;
        WhatsNewList.Items.Clear();
        WhatsNewEmptyHint.Visibility = Visibility.Collapsed;

        IReadOnlyList<WhatsNewService.ReleaseEntry> releases;
        try
        {
            releases = await App.Current.WhatsNew.FetchLatestReleasesAsync(count: 5);
        }
        catch (Exception ex)
        {
            Logger.Warn("WhatsNew load: " + ex.Message);
            releases = Array.Empty<WhatsNewService.ReleaseEntry>();
        }

        WhatsNewSpinner.IsActive = false;
        if (releases.Count == 0)
        {
            WhatsNewEmptyHint.Text = L.T("WhatsNew.Empty");
            WhatsNewEmptyHint.Visibility = Visibility.Visible;
            // Cache freigeben — bei erneutem Öffnen lieber nochmal versuchen
            _whatsNewLoaded = false;
            return;
        }

        foreach (var r in releases)
            WhatsNewList.Items.Add(BuildWhatsNewCard(r));
    }

    private FrameworkElement BuildWhatsNewCard(WhatsNewService.ReleaseEntry r)
    {
        var L = App.Current.Loc;
        var monoFont = (FontFamily)Application.Current.Resources["VAFontMono"];
        var sansFont = (FontFamily)Application.Current.Resources["VAFont"];
        var accent = (Brush)Application.Current.Resources["VAAccentBrush"];
        var text = (Brush)Application.Current.Resources["VATextBrush"];
        var dim = (Brush)Application.Current.Resources["VATextDimBrush"];
        var mute = (Brush)Application.Current.Resources["VATextMuteBrush"];
        var borderSoft = (Brush)Application.Current.Resources["VABorderSoftBrush"];

        var card = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = borderSoft,
            Background = (Brush)Application.Current.Resources["VABgRaisedBrush"],
            Margin = new Thickness(0, 0, 0, 10),
        };
        var stack = new StackPanel { Spacing = 6 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new TextBlock
        {
            Text = r.Title,
            FontFamily = sansFont, FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = text,
        });
        if (!string.IsNullOrEmpty(r.TagName))
        {
            header.Children.Add(new Border
            {
                BorderBrush = accent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = r.TagName, FontFamily = monoFont, FontSize = 10,
                    Foreground = accent,
                },
            });
        }
        stack.Children.Add(header);

        if (r.PublishedAt != DateTime.MinValue)
        {
            stack.Children.Add(new TextBlock
            {
                Text = r.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                FontFamily = monoFont, FontSize = 10.5, Foreground = mute,
            });
        }

        var headline = WhatsNewService.ExtractHeadline(r.Body);
        if (!string.IsNullOrEmpty(headline) && !string.Equals(headline, r.Title, StringComparison.OrdinalIgnoreCase))
        {
            stack.Children.Add(new TextBlock
            {
                Text = headline,
                FontFamily = monoFont, FontSize = 11.5,
                Foreground = dim, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        var bullets = WhatsNewService.ExtractBulletLines(r.Body);
        var hasMore = bullets.Count > 5;
        if (bullets.Count > 0)
        {
            var listStack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var b in bullets.Take(5))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock
                {
                    Text = "•", FontFamily = monoFont, FontSize = 13,
                    Foreground = accent, VerticalAlignment = VerticalAlignment.Top,
                });
                row.Children.Add(new TextBlock
                {
                    Text = b, FontFamily = sansFont, FontSize = 13,
                    Foreground = dim, TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 720,
                });
                listStack.Children.Add(row);
            }
            if (hasMore)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = string.Format(L.T("WhatsNew.MoreFmt"), bullets.Count - 5),
                    FontFamily = monoFont, FontSize = 10.5,
                    Foreground = mute, Margin = new Thickness(20, 4, 0, 0),
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                });
            }
            stack.Children.Add(listStack);
        }

        if (!string.IsNullOrEmpty(r.HtmlUrl))
        {
            var link = new Button
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 0),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = new TextBlock
                {
                    Text = L.T("WhatsNew.ReadFull"),
                    FontFamily = monoFont, FontSize = 11,
                    Foreground = accent,
                },
            };
            link.Click += async (_, _) =>
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(r.HtmlUrl)); }
                catch (Exception ex) { Logger.Warn("WhatsNew link: " + ex.Message); }
            };
            stack.Children.Add(link);
        }

        card.Child = stack;
        return card;
    }

    // ============================= ABOUT =============================

    private void RenderAbout()
    {
        var L = App.Current.Loc;
        AboutSectionLabel.Text = L.T("About.Section");
        AboutTitle.Text = L.T("About.Title");
        AboutVersionText.Text = string.Format(L.T("About.VersionFmt"), WhatsNewService.CurrentVersion());
        AboutTagline.Text = L.T("About.Tagline");
        AboutAuthorLabel.Text = L.T("About.AuthorLabel");
        AboutAuthorValue.Text = L.T("About.AuthorValue");
        AboutLinksLabel.Text = L.T("About.LinksLabel");
        AboutGithubLabel.Text = L.T("About.Btn.Github");
        AboutBugLabel.Text = L.T("About.Btn.Bug");
        AboutFeatureLabel.Text = L.T("About.Btn.Feature");
        AboutBugHint.Text = L.T("About.BugHint");
    }

    private async void OnAboutOpenGithub(object sender, RoutedEventArgs e)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(WhatsNewService.RepoUrl)); }
        catch (Exception ex) { Logger.Warn("About.Github: " + ex.Message); }
    }

    private async void OnAboutReportBug(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = $"{WhatsNewService.RepoUrl.TrimEnd('/')}/issues/new?template=bug_report.yml";
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex) { Logger.Warn("About.Bug: " + ex.Message); }
    }

    private async void OnAboutFeatureRequest(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = $"{WhatsNewService.RepoUrl.TrimEnd('/')}/issues/new?template=feature_request.yml";
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex) { Logger.Warn("About.Feature: " + ex.Message); }
    }

    private string _activeSettingsTab = "connection";

    private void OnSettingsNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag) ApplySettingsTab(tag);
    }

    private void ApplySettingsTab(string tab)
    {
        _activeSettingsTab = tab;
        var L = App.Current.Loc;

        ConnectionCard.Visibility = tab == "connection" ? Visibility.Visible : Visibility.Collapsed;
        RecordingCard.Visibility = tab == "recording" ? Visibility.Visible : Visibility.Collapsed;
        SoundsCard.Visibility = tab == "sounds" ? Visibility.Visible : Visibility.Collapsed;
        SystemCard.Visibility = tab == "system" ? Visibility.Visible : Visibility.Collapsed;

        // Aktive Highlights
        HighlightNav(NavSettingsConnection, tab == "connection");
        HighlightNav(NavSettingsRecording, tab == "recording");
        HighlightNav(NavSettingsSounds, tab == "sounds");
        HighlightNav(NavSettingsSystem, tab == "system");

        // Card-Header (Titel + Subtitle)
        SettingsTitle.Text = tab switch
        {
            "connection" => L.T("Settings.Card.Connection"),
            "recording" => L.T("Settings.Card.Recording"),
            "sounds" => L.T("Settings.Card.Sounds"),
            "system" => L.T("Settings.Card.System"),
            _ => "",
        };
        SettingsSubtitle.Text = tab switch
        {
            "connection" => L.T("Settings.Subtitle.Connection"),
            "recording" => L.T("Settings.Subtitle.Recording"),
            "sounds" => L.T("Settings.Subtitle.Sounds"),
            "system" => L.T("Settings.Subtitle.System"),
            _ => "",
        };
    }

    private void HighlightNav(Button btn, bool active)
    {
        if (active)
        {
            btn.Background = (Brush)Application.Current.Resources["VASurfaceBrush"];
            btn.BorderBrush = (Brush)Application.Current.Resources["VABorderSoftBrush"];
            btn.BorderThickness = new Thickness(1);
        }
        else
        {
            btn.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.BorderThickness = new Thickness(0);
        }
    }

    private void OnTempChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ServerTempLabel.Text = string.Format(App.Current.Loc.T("Profile.TempFmt"), ServerTempSlider.Value);
    }

    private async void OnProfileLoad(object sender, RoutedEventArgs e) => await LoadProfileAsync(force: true);

    private HashSet<string> _installedModels = new();

    private async Task LoadProfileAsync(bool force = false)
    {
        var s = App.Current.Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey))
        {
            ProfileStatus.Text = App.Current.Loc.T("Profile.NoApiKey");
            return;
        }
        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            ProfileStatus.Text = App.Current.Loc.T("Profile.Loading");

            // Modell-Liste
            var models = await App.Current.ServerApi.ListModelsAsync(baseUrl, s.ApiKey);
            _installedModels = new HashSet<string>(models.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

            ServerModelCombo.Items.Clear();
            ServerModelCombo.Items.Add("(Server-Default)");
            foreach (var m in models) ServerModelCombo.Items.Add(m.Id);

            // Profil
            var p = await App.Current.ServerApi.GetProfileAsync(baseUrl, s.ApiKey);
            ProfileApiKeyLabel.Text = string.Format(
                App.Current.Loc.T("Profile.ApiKeyFmt"), p.ApiKeyName, p.ApiKeyId);

            // Auto-Fix: das aktuell effektive Modell prüfen.
            //   - p.Model gesetzt UND nicht installiert  → veralteter Eintrag, auto-korrigieren
            //   - p.Model null UND Server-Default nicht installiert → auf erstes installiertes umschalten
            // In beiden Fällen wählen wir das erste installierte Modell und speichern es im Profil.
            var effective = string.IsNullOrEmpty(p.Model) ? p.ServerDefaultModel : p.Model;
            var effectiveInstalled = !string.IsNullOrEmpty(effective)
                && _installedModels.Contains(effective);
            if (!effectiveInstalled && _installedModels.Count > 0)
            {
                var firstInstalled = _installedModels.First();
                Logger.Info($"Profile auto-fix: effective='{effective}' not installed, switching to '{firstInstalled}'");
                try
                {
                    p = await App.Current.ServerApi.UpdateProfileAsync(
                        baseUrl, s.ApiKey, model: firstInstalled, prompt: null, temperature: null);
                    ProfileStatus.Text = string.Format(
                        App.Current.Loc.T("Profile.AutoSwitchedFmt"), firstInstalled);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Profile auto-switch failed: " + ex.Message);
                }
            }

            if (string.IsNullOrEmpty(p.Model))
                ServerModelCombo.SelectedIndex = 0;
            else
            {
                var idx = ServerModelCombo.Items.IndexOf(p.Model);
                ServerModelCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            // Sprach-spezifische Prompts laden. Legacy: hat der User noch keinen DE-Prompt
            // gesetzt, aber das alte `prompt`-Feld ist gefüllt → in DE migrieren (User ist
            // laut Profil deutschsprachig). Persistiert wird das erst beim nächsten Save.
            var promptDe = p.PromptDe ?? "";
            var promptEn = p.PromptEn ?? "";
            if (string.IsNullOrWhiteSpace(promptDe) && string.IsNullOrWhiteSpace(promptEn)
                && !string.IsNullOrWhiteSpace(p.Prompt))
            {
                promptDe = p.Prompt!;
            }
            ServerPromptDeBox.Text = promptDe;
            ServerPromptEnBox.Text = promptEn;
            UpdatePromptPreviewForCurrentLanguage(promptDe, promptEn);
            ServerTempSlider.Value = p.Temperature;
            ServerTempLabel.Text = string.Format(App.Current.Loc.T("Profile.TempFmt"), p.Temperature);

            BuildCatalogList();
            UpdateProfileStatCards(p);

            // Beim ersten Profil-Load: Top-Whisper-Modelle von HF anzeigen (leerer Query = Top-Liste)
            if (HfSearchResults.Items.Count == 0)
                _ = RunHfSearchAsync(HfSearchBox.Text ?? "");

            ProfileStatus.Text = App.Current.Loc.T("Status.Online");
        }
        catch (Exception ex)
        {
            Logger.Error("Profile load", ex);
            ProfileStatus.Text = string.Format(App.Current.Loc.T("Status.ErrorFmt"), ex.Message);
        }
    }

    private void BuildCatalogList()
    {
        ModelCatalogList.Items.Clear();

        // Nur die tatsächlich auf dem Server installierten Modelle anzeigen.
        // Modell-Spezifikationen (Größe, RTF, VRAM) werden aus dem Namen heuristisch geschätzt.
        foreach (var modelId in _installedModels)
        {
            var slashIdx = modelId.IndexOf('/');
            var shortName = slashIdx > 0 ? modelId[(slashIdx + 1)..] : modelId;
            var specs = EstimateModelSpecs(modelId);
            var adHoc = new ModelCatalog.Entry(
                Id: modelId,
                ShortName: shortName,
                Tag: "installed",
                Label: shortName,
                SizeApprox: specs.SizeApprox,
                Rtf: specs.Rtf,
                Hint: "");
            ModelCatalogList.Items.Add(BuildCatalogRow(adHoc));
        }

        // Wenn nichts installiert ist → kleiner Hinweis, dass die Suche darunter genutzt werden kann
        ModelsEmptyHint.Visibility = _installedModels.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private string _activeModelId = "";

    private void UpdateProfileStatCards(ServerApiClient.Profile p)
    {
        var L = App.Current.Loc;
        // Stat 1: Active model
        var activeEntry = ModelCatalog.All.FirstOrDefault(e =>
            string.Equals(e.Id, p.Model, StringComparison.OrdinalIgnoreCase));
        StatActiveModelValue.Text = activeEntry?.ShortName
            ?? (string.IsNullOrEmpty(p.Model) ? L.T("Profile.ModelDefault") : p.Model);
        StatActiveModelTag.Text = activeEntry?.Tag ?? "";
        _activeModelId = p.Model ?? "";

        // Stat 2: Temperature
        StatTempValue.Text = p.Temperature.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        StatTempSub.Text = p.Temperature < 0.05 ? L.T("Profile.TempDeterministic") : L.T("Profile.TempVaried");

        // Stat 3: Prompt word count
        var promptWords = string.IsNullOrWhiteSpace(p.Prompt) ? 0
            : p.Prompt.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        StatPromptValue.Text = promptWords.ToString();
        StatPromptSub.Text = L.T("Profile.PromptWordsBiased");

        // Stat 4: API-Key
        StatApiKeyValue.Text = $"#{p.ApiKeyId}";
        StatApiKeyName.Text = p.ApiKeyName ?? "";
    }

    private FrameworkElement BuildCatalogRow(ModelCatalog.Entry entry)
    {
        var L = App.Current.Loc;
        var installed = _installedModels.Contains(entry.Id);
        var isActive = string.Equals(entry.Id, _activeModelId, StringComparison.OrdinalIgnoreCase);

        var accent = (Brush)Application.Current.Resources["VAAccentBrush"];
        var borderSoft = (Brush)Application.Current.Resources["VABorderSoftBrush"];
        var mute = (Brush)Application.Current.Resources["VATextMuteBrush"];
        var dim = (Brush)Application.Current.Resources["VATextDimBrush"];
        var text = (Brush)Application.Current.Resources["VATextBrush"];
        var ok = (Brush)Application.Current.Resources["VAOkBrush"];
        var warn = (Brush)Application.Current.Resources["VAWarnBrush"];
        var monoFont = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VAFontMono"];
        var sansFont = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VAFont"];

        var card = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = isActive ? accent : borderSoft,
            Background = isActive
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x18, ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B))
                : (Brush)Application.Current.Resources["VABgRaisedBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var stack = new StackPanel { Spacing = 4 };

        // Name + (optional) AKTIV badge
        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameTb = new TextBlock
        {
            Text = entry.ShortName, FontFamily = monoFont,
            FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = isActive ? accent : text,
        };
        Grid.SetColumn(nameTb, 0);
        nameRow.Children.Add(nameTb);
        if (isActive)
        {
            var badge = new Border
            {
                BorderBrush = accent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = L.T("Profile.Active"),
                    FontFamily = monoFont, FontSize = 9,
                    Foreground = accent, CharacterSpacing = 60,
                },
            };
            Grid.SetColumn(badge, 1);
            nameRow.Children.Add(badge);
        }
        stack.Children.Add(nameRow);

        stack.Children.Add(new TextBlock
        {
            Text = entry.Tag, FontFamily = monoFont, FontSize = 10.5,
            Foreground = mute,
        });

        // Size + RTF
        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 8, 0, 0) };
        metaRow.Children.Add(new TextBlock { Text = entry.SizeApprox, FontFamily = monoFont, FontSize = 11, Foreground = dim });
        metaRow.Children.Add(new TextBlock { Text = $"RTF {entry.Rtf:F2}×", FontFamily = monoFont, FontSize = 11, Foreground = dim });
        stack.Children.Add(metaRow);

        // RTF progress-bar
        var rtfTrack = new Border
        {
            Height = 3, CornerRadius = new CornerRadius(2),
            Background = borderSoft, Margin = new Thickness(0, 6, 0, 0),
        };
        var fillWidth = Math.Min(100.0, entry.Rtf * 30 + 10) / 100.0;
        var rtfFillContainer = new Grid();
        rtfFillContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fillWidth, GridUnitType.Star) });
        rtfFillContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fillWidth, GridUnitType.Star) });
        var rtfFill = new Border
        {
            Background = entry.Rtf > 1.5 ? warn : (entry.Rtf > 0.8 ? accent : ok),
            CornerRadius = new CornerRadius(2),
        };
        Grid.SetColumn(rtfFill, 0);
        rtfFillContainer.Children.Add(rtfFill);
        rtfTrack.Child = rtfFillContainer;
        stack.Children.Add(rtfTrack);

        // Action: installed pill or Install button
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        if (installed)
        {
            actionRow.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = ok });
            actionRow.Children.Add(new TextBlock
            {
                Text = L.T("Models.Installed"), FontFamily = monoFont,
                FontSize = 11, Foreground = ok, VerticalAlignment = VerticalAlignment.Center,
            });
            // Uninstall-Button — nicht für das gerade aktive Modell anbieten
            if (!isActive)
            {
                var delBtn = new Button
                {
                    Padding = new Thickness(6, 4, 6, 4),
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon { Glyph = "", FontSize = 13, Foreground = mute },
                };
                ToolTipService.SetToolTip(delBtn, L.T("Models.Btn.Uninstall"));
                delBtn.Click += async (_, _) => await UninstallModelAsync(entry, delBtn);
                actionRow.Children.Add(delBtn);
            }
        }
        else
        {
            var progress = new ProgressRing { IsActive = false, Width = 14, Height = 14 };
            var btn = new Button
            {
                Content = L.T("Models.Btn.Install"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11,
            };
            btn.Click += async (_, _) => await InstallModelAsync(entry, btn, progress);
            actionRow.Children.Add(progress);
            actionRow.Children.Add(btn);
        }
        stack.Children.Add(actionRow);

        card.Child = stack;
        return card;
    }

    private async Task InstallModelAsync(ModelCatalog.Entry entry, Button btn, ProgressRing progress)
    {
        var s = App.Current.Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey)) return;

        var L = App.Current.Loc;
        btn.IsEnabled = false;
        btn.Content = L.T("Models.Btn.Loading");
        progress.IsActive = true;

        // Polling-Task: prüft alle 5 s, ob das Modell schon in der lokalen Liste auftaucht.
        // Gibt dem User Feedback, falls die POST-Antwort sehr lange braucht.
        using var cts = new CancellationTokenSource();
        var pollTask = Task.Run(async () =>
        {
            var startedAt = DateTime.Now;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
                    var models = await App.Current.ServerApi.ListModelsAsync(baseUrl, s.ApiKey, cts.Token);
                    if (models.Any(m => string.Equals(m.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                        return; // installiert!
                }
                catch { /* ignorieren, wir versuchen's weiter */ }
                var elapsed = (int)(DateTime.Now - startedAt).TotalSeconds;
                DispatcherQueue.TryEnqueue(() => btn.Content = string.Format(L.T("Models.Btn.LoadingFmt"), elapsed));
                try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); } catch { }
            }
        });

        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            await App.Current.ServerApi.InstallModelAsync(baseUrl, s.ApiKey, entry.Id);
            cts.Cancel();
            progress.IsActive = false;
            btn.Content = L.T("Models.Btn.Done");
            await Task.Delay(800);
            await LoadProfileAsync(force: true); // Liste neu aufbauen
        }
        catch (Exception ex)
        {
            cts.Cancel();
            Logger.Error("Model install", ex);
            progress.IsActive = false;
            btn.IsEnabled = true;
            btn.Content = L.T("Models.Btn.Retry");
            ProfileStatus.Text = string.Format(L.T("Models.ErrorFmt"), ex.Message);
        }
    }

    private async Task UninstallModelAsync(ModelCatalog.Entry entry, Button btn)
    {
        var s = App.Current.Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey)) return;
        var L = App.Current.Loc;

        // Confirm-Dialog — Modelle sind groß und Re-Download dauert
        var dlg = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = L.T("Models.Confirm.UninstallTitle"),
            Content = string.Format(L.T("Models.Confirm.UninstallBodyFmt"), entry.Label, entry.SizeApprox),
            PrimaryButtonText = L.T("Models.Btn.Uninstall"),
            CloseButtonText = L.T("Models.Btn.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        btn.IsEnabled = false;
        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            await App.Current.ServerApi.UninstallModelAsync(baseUrl, s.ApiKey, entry.Id);
            await LoadProfileAsync(force: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Model uninstall", ex);
            btn.IsEnabled = true;
            ProfileStatus.Text = string.Format(L.T("Models.UninstallErrorFmt"), ex.Message);
        }
    }

    // ===================== HuggingFace-Suche =====================

    private void OnHfSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Debounce: warte 400 ms nach der letzten Tastatur-Eingabe, dann suchen
        _hfSearchDebounce ??= DispatcherQueue.CreateTimer();
        _hfSearchDebounce.Interval = TimeSpan.FromMilliseconds(400);
        _hfSearchDebounce.IsRepeating = false;
        _hfSearchDebounce.Tick -= HfSearchTick;
        _hfSearchDebounce.Tick += HfSearchTick;
        _hfSearchDebounce.Start();
    }

    private async void HfSearchTick(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object e)
    {
        t.Stop();
        await RunHfSearchAsync(HfSearchBox.Text);
    }

    private async Task RunHfSearchAsync(string query)
    {
        // Vorigen Lauf canceln, damit der jüngste Tastendruck gewinnt
        _hfSearchCts?.Cancel();
        _hfSearchCts = new CancellationTokenSource();
        var ct = _hfSearchCts.Token;

        HfSearchSpinner.IsActive = true;
        HfSearchEmpty.Visibility = Visibility.Collapsed;

        IReadOnlyList<HuggingFaceSearchClient.SearchResult> results;
        try
        {
            results = await App.Current.HfSearch.SearchAsync(query, limit: 24, ct);
        }
        catch (Exception ex)
        {
            Logger.Warn("HF search exception: " + ex.Message);
            results = Array.Empty<HuggingFaceSearchClient.SearchResult>();
        }

        if (ct.IsCancellationRequested) return;
        HfSearchSpinner.IsActive = false;
        RenderHfResults(results);
    }

    private void RenderHfResults(IReadOnlyList<HuggingFaceSearchClient.SearchResult> results)
    {
        var L = App.Current.Loc;
        HfSearchResults.Items.Clear();

        // Schon installierte Modelle filtern — die werden oben in der "Whisper-Cache"-Sektion gezeigt
        var hidden = new HashSet<string>(_installedModels, StringComparer.OrdinalIgnoreCase);

        var filtered = results
            .Where(r => !hidden.Contains(r.Id))
            .ToList();

        if (filtered.Count == 0)
        {
            HfSearchEmpty.Text = L.T("HfSearch.NoResults");
            HfSearchEmpty.Visibility = Visibility.Visible;
            return;
        }

        foreach (var r in filtered)
            HfSearchResults.Items.Add(BuildHfResultCard(r));
    }

    private FrameworkElement BuildHfResultCard(HuggingFaceSearchClient.SearchResult r)
    {
        var L = App.Current.Loc;
        var installed = _installedModels.Contains(r.Id);

        var borderSoft = (Brush)Application.Current.Resources["VABorderSoftBrush"];
        var mute = (Brush)Application.Current.Resources["VATextMuteBrush"];
        var dim = (Brush)Application.Current.Resources["VATextDimBrush"];
        var text = (Brush)Application.Current.Resources["VATextBrush"];
        var ok = (Brush)Application.Current.Resources["VAOkBrush"];
        var accent = (Brush)Application.Current.Resources["VAAccentBrush"];
        var warn = (Brush)Application.Current.Resources["VAWarnBrush"];
        var monoFont = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VAFontMono"];

        var card = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = borderSoft,
            Background = (Brush)Application.Current.Resources["VABgRaisedBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var stack = new StackPanel { Spacing = 4 };

        // Repo-ID als anklickbarer Bereich → öffnet HuggingFace-Modell-Seite
        var slashIdx = r.Id.IndexOf('/');
        var org = slashIdx > 0 ? r.Id[..slashIdx] : "";
        var name = slashIdx > 0 ? r.Id[(slashIdx + 1)..] : r.Id;

        var idPanel = new StackPanel { Spacing = 0 };
        if (!string.IsNullOrEmpty(org))
        {
            idPanel.Children.Add(new TextBlock
            {
                Text = org + "/", FontFamily = monoFont, FontSize = 10.5,
                Foreground = mute, TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
        };
        nameRow.Children.Add(new TextBlock
        {
            Text = name, FontFamily = monoFont, FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = accent,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        });
        // Kleines "open-in-new"-Glyph als visueller Link-Hinweis (Segoe MDL2: OpenInNewWindow = E8A7)
        nameRow.Children.Add(new FontIcon
        {
            Glyph = "", FontSize = 10,
            Foreground = mute, VerticalAlignment = VerticalAlignment.Center,
        });
        idPanel.Children.Add(nameRow);

        // Klick auf den Namen → HF-Seite im Default-Browser
        var linkButton = new Button
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = idPanel,
        };
        ToolTipService.SetToolTip(linkButton,
            string.Format(L.T("HfSearch.OpenOnHfFmt"), r.Id));
        linkButton.Click += async (_, _) =>
        {
            try
            {
                var uri = new Uri($"https://huggingface.co/{r.Id}");
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                Logger.Warn("HF link launch failed: " + ex.Message);
            }
        };
        stack.Children.Add(linkButton);

        // Heuristische Schätzung: Modellgröße + RTF aus dem Namen ableiten
        var specs = EstimateModelSpecs(r.Id);

        // Stats: Downloads + Likes
        stack.Children.Add(new TextBlock
        {
            Text = string.Format(L.T("HfSearch.StatsFmt"),
                                 FormatCount(r.Downloads), FormatCount(r.Likes)),
            FontFamily = monoFont, FontSize = 10.5, Foreground = dim,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // Size + RTF (genau wie in BuildCatalogRow)
        var metaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        metaRow.Children.Add(new TextBlock
        {
            Text = specs.SizeApprox, FontFamily = monoFont, FontSize = 11, Foreground = dim,
        });
        metaRow.Children.Add(new TextBlock
        {
            Text = $"RTF {specs.Rtf:F2}×", FontFamily = monoFont, FontSize = 11, Foreground = dim,
        });
        stack.Children.Add(metaRow);

        // RTF-Bar
        var rtfTrack = new Border
        {
            Height = 3, CornerRadius = new CornerRadius(2),
            Background = borderSoft, Margin = new Thickness(0, 4, 0, 0),
        };
        var fillWidth = Math.Min(100.0, specs.Rtf * 30 + 10) / 100.0;
        var rtfFillContainer = new Grid();
        rtfFillContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fillWidth, GridUnitType.Star) });
        rtfFillContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fillWidth, GridUnitType.Star) });
        var rtfFill = new Border
        {
            Background = specs.Rtf > 1.5 ? warn : (specs.Rtf > 0.8 ? accent : ok),
            CornerRadius = new CornerRadius(2),
        };
        Grid.SetColumn(rtfFill, 0);
        rtfFillContainer.Children.Add(rtfFill);
        rtfTrack.Child = rtfFillContainer;
        stack.Children.Add(rtfTrack);

        // VRAM-Schätzung
        if (!string.IsNullOrEmpty(specs.VramHint))
        {
            stack.Children.Add(new TextBlock
            {
                Text = specs.VramHint, FontFamily = monoFont, FontSize = 10.5,
                Foreground = mute, Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Tags — max 3, gefiltert
        if (r.Tags is { Length: > 0 })
        {
            var relevant = r.Tags
                .Where(t => !t.StartsWith("license:", StringComparison.OrdinalIgnoreCase)
                         && !t.StartsWith("region:", StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToArray();
            if (relevant.Length > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", relevant),
                    FontFamily = monoFont, FontSize = 10, Foreground = mute,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
        }

        // Action row
        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        if (installed)
        {
            actionRow.Children.Add(new TextBlock
            {
                Text = L.T("Models.Installed"), FontFamily = monoFont,
                FontSize = 11, Foreground = ok, VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            var progress = new ProgressRing { IsActive = false, Width = 14, Height = 14 };
            var btn = new Button
            {
                Content = L.T("Models.Btn.Install"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11,
            };
            var adHocEntry = new ModelCatalog.Entry(
                Id: r.Id,
                ShortName: name,
                Tag: "huggingface",
                Label: name,
                SizeApprox: specs.SizeApprox,
                Rtf: specs.Rtf,
                Hint: "");
            btn.Click += async (_, _) => await InstallModelAsync(adHocEntry, btn, progress);
            actionRow.Children.Add(progress);
            actionRow.Children.Add(btn);
        }
        stack.Children.Add(actionRow);

        card.Child = stack;
        return card;
    }

    /// <summary>
    /// Schätzt Modell-Größe (Repo auf Disk) + RTF + VRAM-Verbrauch heuristisch aus dem Repo-Namen.
    /// Keine echten Werte, aber für faster-whisper-Modelle reicht die Naming-Convention.
    /// </summary>
    private static (string SizeApprox, double Rtf, string VramHint) EstimateModelSpecs(string id)
    {
        var L = App.Current.Loc;
        var n = id.ToLowerInvariant();
        bool isDistil = n.Contains("distil");
        bool isTurbo = n.Contains("turbo");

        // Reihenfolge: spezifischere Matches zuerst
        if (n.Contains("large") && isTurbo)
            return ("~1.5 GB", 0.80, FormatVram(L, 1.2));
        if (isDistil && n.Contains("large"))
            return ("~1.5 GB", 1.10, FormatVram(L, 0.9));
        if (n.Contains("large"))
            return ("~3 GB", 3.10, FormatVram(L, 1.6));
        if (isDistil && n.Contains("medium"))
            return ("~0.8 GB", 0.70, FormatVram(L, 0.5));
        if (n.Contains("medium"))
            return ("~1.5 GB", 0.91, FormatVram(L, 0.8));
        if (isDistil && n.Contains("small"))
            return ("~0.4 GB", 0.40, FormatVram(L, 0.3));
        if (n.Contains("small"))
            return ("~0.5 GB", 0.55, FormatVram(L, 0.3));
        if (n.Contains("base"))
            return ("~150 MB", 0.25, FormatVram(L, 0.12));
        if (n.Contains("tiny"))
            return ("~75 MB", 0.15, FormatVram(L, 0.07));

        return ("?", 1.0, L.T("HfSearch.UnknownSize"));
    }

    private static string FormatVram(LocalizationService L, double gb) =>
        string.Format(L.T("HfSearch.VramFmt"),
            gb >= 1 ? $"~{gb:F1} GB" : $"~{(int)(gb * 1000)} MB");

    private static string FormatCount(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString(),
    };

    // ============== Audio-File-Upload (Drag & Drop + Browse) ==============

    private static readonly string[] _supportedAudioExts =
    {
        ".wav", ".mp3", ".m4a", ".mp4", ".ogg", ".opus", ".flac", ".aac", ".webm", ".wma",
    };

    private void OnUploadDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            // Visual: leichte Akzent-Border, damit klar ist "hier kann ich ablegen"
            UploadDropZone.BorderBrush = (Brush)Application.Current.Resources["VAAccentBrush"];
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = App.Current.Loc.T("Upload.DropHint");
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
        e.Handled = true;
    }

    private async void OnUploadDrop(object sender, DragEventArgs e)
    {
        UploadDropZone.BorderBrush = (Brush)Application.Current.Resources["VABorderSoftBrush"];
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<Windows.Storage.StorageFile>()
                        .FirstOrDefault(f => _supportedAudioExts.Contains(
                            System.IO.Path.GetExtension(f.Name).ToLowerInvariant()));
        if (file is null)
        {
            HomeStatus.Text = App.Current.Loc.T("Upload.UnsupportedFormat");
            return;
        }
        await StartUploadAsync(file.Path);
    }

    private async void OnUploadBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
        foreach (var ext in _supportedAudioExts) picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await StartUploadAsync(file.Path);
    }

    private async void OnUploadBrowse(object sender, TappedRoutedEventArgs e)
    {
        // Tap auf die DropZone (außerhalb des Browse-Buttons) öffnet ebenfalls den Picker
        if (e.OriginalSource is Button) return;
        await Task.Yield();
        OnUploadBrowse((object)this, new RoutedEventArgs());
    }

    private async Task StartUploadAsync(string path)
    {
        if (App.Current.Coordinator.IsBusy || App.Current.Coordinator.IsRecording)
        {
            HomeStatus.Text = App.Current.Loc.T("Upload.BusyHint");
            return;
        }
        await App.Current.Coordinator.TranscribeFileAsync(path);
    }

    private async void OnProfileSave(object sender, RoutedEventArgs e)
    {
        var s = App.Current.Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey))
        {
            ProfileStatus.Text = App.Current.Loc.T("Profile.NoApiKey");
            return;
        }
        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            string? model = null;
            if (ServerModelCombo.SelectedItem is string m && ServerModelCombo.SelectedIndex > 0)
                model = m;
            var promptDe = ServerPromptDeBox.Text;
            var promptEn = ServerPromptEnBox.Text;
            var temp = ServerTempSlider.Value;
            // Legacy `prompt`-Feld leeren — die Sprach-Varianten ersetzen es jetzt.
            await App.Current.ServerApi.UpdateProfileAsync(
                baseUrl, s.ApiKey, model ?? "", prompt: "", temperature: temp,
                promptDe: promptDe, promptEn: promptEn);
            UpdatePromptPreviewForCurrentLanguage(promptDe, promptEn);
            ProfileStatus.Text = string.Format(App.Current.Loc.T("Status.SavedFmt"), DateTime.Now);
        }
        catch (Exception ex)
        {
            Logger.Error("Profile save", ex);
            ProfileStatus.Text = string.Format(App.Current.Loc.T("Status.ErrorFmt"), ex.Message);
        }
    }

    private void OnCopyTranscript(object sender, RoutedEventArgs e)
    {
        var text = TranscriptBox.Text;
        var L = App.Current.Loc;
        if (string.IsNullOrEmpty(text))
        {
            HomeStatus.Text = L.T("Home.Status.Empty");
            return;
        }
        App.Current.Output.CopyToClipboard(text);
        HomeStatus.Text = string.Format(L.T("Home.Status.CopiedFmt"), text.Length);
    }

    private void OnClearTranscript(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Text = "";
        HomeStatus.Text = App.Current.Loc.T("Home.Status.Cleared");
    }

    private void OnHotkeyEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoSave) return;
        var enabled = HotkeyEnabledToggle.IsOn;
        App.Current.Settings.HotkeyEnabled = enabled;
        App.Current.SaveSettings(); // ApplyHotkey wird darin aufgerufen
        HomeStatus.Text = App.Current.Loc.T(enabled ? "Home.Hotkey.On" : "Home.Hotkey.Off");
    }

    // ============================= HERO STATE =============================

    private void UpdateHeroState()
    {
        var L = App.Current.Loc;
        var recording = App.Current.Coordinator.IsRecording;
        if (recording)
        {
            HeroDot.Fill = (Brush)Application.Current.Resources["VADangerBrush"];
            HeroStateLabel.Text = L.T("Hero.Recording");
            HeroStateLabel.Foreground = (Brush)Application.Current.Resources["VADangerBrush"];
            HeroHeadline.Visibility = Visibility.Collapsed;
            HeroTimer.Visibility = Visibility.Visible;
            HeroHotkeyHintLabel.Text = L.T("Hero.HotkeyStop");
            HotkeyKbd.Visibility = Visibility.Visible;
            HotkeyKbdLabel.Text = App.Current.Settings.HotkeyDisplay();
            HeroHotkeyTrailLabel.Text = "";
            MicStopIcon.Visibility = Visibility.Visible;
            MicIcon.Visibility = Visibility.Collapsed;
            MicButtonGradient.GradientStops[0].Color = ThemeAccentColor();
            MicButtonGradient.GradientStops[1].Color = Microsoft.UI.Colors.Black;
            HeroWave.IsActive = true;
            if (_recStartedAt == DateTime.MinValue) _recStartedAt = DateTime.Now;
            _recTimer?.Start();
            UpdateHeroTimer();
        }
        else
        {
            HeroDot.Fill = (Brush)Application.Current.Resources["VATextMuteBrush"];
            HeroStateLabel.Text = L.T("Hero.Idle");
            HeroStateLabel.Foreground = (Brush)Application.Current.Resources["VATextMuteBrush"];
            HeroHeadline.Visibility = Visibility.Visible;
            HeroHeadline.Text = L.T("Hero.StartHeadline");
            HeroTimer.Visibility = Visibility.Collapsed;
            HeroHotkeyHintLabel.Text = L.T("Hero.HotkeyHint");
            HotkeyKbd.Visibility = Visibility.Visible;
            HotkeyKbdLabel.Text = App.Current.Settings.HotkeyDisplay();
            HeroHotkeyTrailLabel.Text = L.T("Hero.OrClick");
            MicStopIcon.Visibility = Visibility.Collapsed;
            MicIcon.Visibility = Visibility.Visible;
            MicButtonGradient.GradientStops[0].Color = ((SolidColorBrush)Application.Current.Resources["VASurfaceHiBrush"]).Color;
            MicButtonGradient.GradientStops[1].Color = ((SolidColorBrush)Application.Current.Resources["VABgBrush"]).Color;
            HeroWave.IsActive = false;
            _recStartedAt = DateTime.MinValue;
            _recTimer?.Stop();
        }
        HeroMetaLabel.Text = BuildHeroMeta();
        HeroWave.RefreshBrush();
    }

    private void UpdateHeroTimer()
    {
        if (_recStartedAt == DateTime.MinValue) return;
        var s = (int)(DateTime.Now - _recStartedAt).TotalSeconds;
        HeroTimer.Text = $"{s / 60:D2}:{s % 60:D2}";
    }

    private string BuildHeroMeta()
    {
        var s = App.Current.Settings;
        var lang = string.IsNullOrWhiteSpace(s.Language)
            ? App.Current.Loc.T("Settings.Lang.Auto")
            : s.Language.ToUpperInvariant();
        return $"· 16 kHz · {lang}";
    }

    private static Windows.UI.Color ThemeAccentColor() =>
        ((SolidColorBrush)Application.Current.Resources["VAAccentBrush"]).Color;

    // ============================= TRANSCRIPT STATS =============================

    private void UpdateTranscriptStats()
    {
        var L = App.Current.Loc;
        var text = TranscriptBox?.Text ?? "";
        var words = string.IsNullOrWhiteSpace(text) ? 0
            : text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        TranscriptStatsWords.Text = string.Format(L.T("Home.Stats.WordsFmt"), words);
        TranscriptStatsChars.Text = string.Format(L.T("Home.Stats.CharsFmt"), text.Length);
    }

    // ============================= HISTORY =============================

    private void OnHistoryFilterChanged(object sender, TextChangedEventArgs e) => RefreshHistory();

    private void RefreshHistory()
    {
        var L = App.Current.Loc;
        var filter = HistoryFilter?.Text;
        var items = App.Current.History.List(filter, 50);
        HistoryCount.Text = string.Format(L.T("Home.History.CountFmt"), items.Count);
        HistoryList.Items.Clear();
        foreach (var entry in items)
            HistoryList.Items.Add(BuildHistoryRow(entry));
    }

    private FrameworkElement BuildHistoryRow(HistoryService.Entry entry)
    {
        var grid = new Grid
        {
            Padding = new Thickness(11, 9, 11, 9),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 4),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["VABorderSoftBrush"],
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: title + time
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = entry.Title,
            FontFamily = (FontFamily)Application.Current.Resources["VAFont"],
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["VATextBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = entry.Pinned ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        Grid.SetColumn(title, 0);
        var time = new TextBlock
        {
            Text = entry.CreatedAt.ToString("HH:mm"),
            FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"],
            FontSize = 9.5,
            Foreground = (Brush)Application.Current.Resources["VATextMuteBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 1);
        topRow.Children.Add(title);
        topRow.Children.Add(time);

        // Row 1: lang badge + duration + words
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        if (!string.IsNullOrEmpty(entry.Language))
        {
            meta.Children.Add(new Border
            {
                BorderBrush = (Brush)Application.Current.Resources["VABorderSoftBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock
                {
                    Text = entry.Language.ToUpperInvariant(),
                    FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"],
                    FontSize = 9.5,
                    Foreground = (Brush)Application.Current.Resources["VATextMuteBrush"],
                },
            });
        }
        if (entry.DurationMs > 0)
        {
            var s = entry.DurationMs / 1000;
            meta.Children.Add(new TextBlock
            {
                Text = $"{s / 60:D1}:{s % 60:D2}",
                FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"],
                FontSize = 9.5,
                Foreground = (Brush)Application.Current.Resources["VATextMuteBrush"],
            });
            meta.Children.Add(new TextBlock
            {
                Text = "·",
                FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"],
                FontSize = 9.5,
                Foreground = (Brush)Application.Current.Resources["VATextMuteBrush"],
            });
        }
        var wordCount = string.IsNullOrWhiteSpace(entry.Text)
            ? 0
            : entry.Text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        meta.Children.Add(new TextBlock
        {
            Text = $"{wordCount}w",
            FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"],
            FontSize = 9.5,
            Foreground = (Brush)Application.Current.Resources["VATextMuteBrush"],
        });
        Grid.SetRow(meta, 1);

        Grid.SetRow(topRow, 0);
        grid.Children.Add(topRow);
        grid.Children.Add(meta);

        // Click → load into TranscriptBox
        grid.Tapped += (_, _) =>
        {
            TranscriptBox.Text = entry.Text;
            UpdateTranscriptStats();
        };
        return grid;
    }

    private void AutoSave()
    {
        if (_suppressAutoSave) return;
        var s = App.Current.Settings;
        s.ServerUrl = ServerUrlBox.Text.Trim();
        s.ApiKey = ApiKeyBox.Password.Trim();
        var newLang = ReadLanguageSelection();
        if (s.Language != newLang)
        {
            s.Language = newLang;
            // Home-Combo synchronisieren ohne Loop
            var prev = _suppressAutoSave;
            _suppressAutoSave = true;
            try { SetComboByTag(HomeLanguageCombo, newLang); }
            finally { _suppressAutoSave = prev; }
        }

        if (MicCombo.SelectedItem is MicEntry mic)
        {
            s.MicDeviceNumber = mic.Number;
            s.MicDeviceName = mic.Name;
        }

        if (OutputModeCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag &&
            Enum.TryParse<OutputMode>(tag, out var mode))
        {
            if (s.OutputMode != mode)
            {
                s.OutputMode = mode;
                var prev = _suppressAutoSave;
                _suppressAutoSave = true;
                try { SetComboByTag(HomeOutputModeCombo, tag); }
                finally { _suppressAutoSave = prev; }
                UpdateTypingSpeedVisibility();
            }
        }

        s.ShowToastOnResult = ToastToggle.IsOn;

        s.SoundOnStart = ReadSoundCombo(SoundStartCombo);
        s.SoundOnStop = ReadSoundCombo(SoundStopCombo);
        s.SoundOnDone = ReadSoundCombo(SoundDoneCombo);
        s.SoundOnError = ReadSoundCombo(SoundErrorCombo);
        s.SoundVolume = (int)VolumeSlider.Value;

        s.HudEnabled = HudEnabledToggle.IsOn;
        if (HudPositionCombo.SelectedItem is ComboBoxItem hci && hci.Tag is string htag &&
            Enum.TryParse<HudPosition>(htag, out var hpos))
            s.HudPosition = hpos;

        App.Current.SaveSettings();
        App.Current.Hud?.ApplyPosition();
        var msg = string.Format(App.Current.Loc.T("Status.SavedFmt"), DateTime.Now);
        StatusText.Text = msg;
        SettingsSavedIndicator.Text = msg;
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e) => AutoSave();
    private void OnFieldChanged(object sender, object e) => AutoSave();

    private void SetLanguageSelection(string lang)
    {
        SetComboByTag(LanguageCombo, lang ?? "");
        SetComboByTag(HomeLanguageCombo, lang ?? "");
    }

    private static void SetComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == tag) { combo.SelectedItem = item; return; }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private string ReadLanguageSelection() =>
        LanguageCombo.SelectedItem is ComboBoxItem ci ? (string)ci.Tag : "";

    private void OnHomeLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAutoSave) return;
        if (HomeLanguageCombo.SelectedItem is not ComboBoxItem ci) return;
        var newLang = (string)ci.Tag;
        if (App.Current.Settings.Language == newLang) return;
        App.Current.Settings.Language = newLang;
        _suppressAutoSave = true;
        try { SetComboByTag(LanguageCombo, newLang); }
        finally { _suppressAutoSave = false; }
        App.Current.SaveSettings();
        UpdateHeroState();
        // Bei Sprachwechsel die Prompt-Vorschau passend aktualisieren
        _ = PrefetchPromptPreviewAsync();
    }

    private void OnHomeOutputModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAutoSave) return;
        if (HomeOutputModeCombo.SelectedItem is not ComboBoxItem ci) return;
        if (ci.Tag is not string tag) return;
        if (!Enum.TryParse<OutputMode>(tag, out var mode)) return;
        if (App.Current.Settings.OutputMode == mode) return;
        App.Current.Settings.OutputMode = mode;
        _suppressAutoSave = true;
        try { SetComboByTag(OutputModeCombo, tag); }
        finally { _suppressAutoSave = false; }
        App.Current.SaveSettings();
        UpdateTypingSpeedVisibility();
    }

    private void OnTypingSpeedChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressAutoSave) return;
        var cps = (int)TypingSpeedSlider.Value;
        App.Current.Settings.TypingSpeedCps = cps;
        _suppressAutoSave = true;
        try { HomeTypingSpeedSlider.Value = cps; }
        finally { _suppressAutoSave = false; }
        UpdateTypingSpeedValueLabels(cps);
        App.Current.SaveSettings();
    }

    private void OnHomeTypingSpeedChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressAutoSave) return;
        var cps = (int)HomeTypingSpeedSlider.Value;
        App.Current.Settings.TypingSpeedCps = cps;
        _suppressAutoSave = true;
        try { TypingSpeedSlider.Value = cps; }
        finally { _suppressAutoSave = false; }
        UpdateTypingSpeedValueLabels(cps);
        App.Current.SaveSettings();
    }

    private static void ConfigureTypingSpeedSlider(Slider slider, int initialValue)
    {
        // Order matters: Maximum first so Value can sit above 100 default;
        // Value before Minimum so Minimum's coercion does not fight with Value.
        slider.Maximum = 2000;
        slider.Value = initialValue;
        slider.Minimum = 30;
        slider.StepFrequency = 10;
    }

    private void UpdateTypingSpeedValueLabels(int cps)
    {
        var text = string.Format(App.Current.Loc.T("Settings.TypingSpeedFmt"), cps);
        TypingSpeedValueLabel.Text = text;
        HomeTypingSpeedValueLabel.Text = text;
    }

    private void UpdateTypingSpeedVisibility()
    {
        var mode = App.Current.Settings.OutputMode;
        var visible = mode == OutputMode.Type || mode == OutputMode.DirectInsert;
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        TypingSpeedPanel.Visibility = v;
        HomeTypingSpeedPanel.Visibility = v;
    }

    private static void InitSoundCombo(ComboBox combo, SoundChoice selected)
    {
        if (combo.Items.Count == 0)
        {
            foreach (var c in SoundService.AllChoices)
                combo.Items.Add(new ComboBoxItem { Content = SoundService.Display(c), Tag = c });
        }
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is SoundChoice c && c == selected) { combo.SelectedItem = item; return; }
        }
        combo.SelectedIndex = 0;
    }

    private static SoundChoice ReadSoundCombo(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem ci && ci.Tag is SoundChoice c ? c : SoundChoice.None;

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        VolumeLabel.Text = App.Current.Loc.T("Settings.Volume");
        VolumeValueLabel.Text = $"{(int)VolumeSlider.Value}%";
        AutoSave();
    }

    private void OnPlayStart(object sender, RoutedEventArgs e)
        => App.Current.Sounds.Play(ReadSoundCombo(SoundStartCombo), (int)VolumeSlider.Value);

    private void OnPlayStop(object sender, RoutedEventArgs e)
        => App.Current.Sounds.Play(ReadSoundCombo(SoundStopCombo), (int)VolumeSlider.Value);

    private void OnPlayDone(object sender, RoutedEventArgs e)
        => App.Current.Sounds.Play(ReadSoundCombo(SoundDoneCombo), (int)VolumeSlider.Value);

    private void OnPlayError(object sender, RoutedEventArgs e)
        => App.Current.Sounds.Play(ReadSoundCombo(SoundErrorCombo), (int)VolumeSlider.Value);

    private void OnHotkeyRecord(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        HotkeyButton.Content = App.Current.Loc.T("Settings.Hotkey.Recording");
        if (Content is FrameworkElement fe) fe.Focus(FocusState.Programmatic);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        var key = e.Key;
        // Skip pure modifier keys
        if (key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows
            or VirtualKey.RightWindows or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return;

        var coreWin = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        uint mods = 0;
        if (IsDown(VirtualKey.Control)) mods |= 2;
        if (IsDown(VirtualKey.Shift)) mods |= 4;
        if (IsDown(VirtualKey.Menu)) mods |= 1;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= 8;

        var s = App.Current.Settings;
        s.HotkeyModifiers = mods;
        s.HotkeyVirtualKey = (uint)key;
        HotkeyDisplay.Text = s.HotkeyDisplay();
        HotkeyButton.Content = App.Current.Loc.T("Settings.Hotkey.Record");
        _recordingHotkey = false;
        AutoSave();
    }

    private static bool IsDown(VirtualKey key)
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void OnMinimizeToTray(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        // Verstecke statt zu beenden — über Tray-Menü "Beenden" setzen wir _quitting=true
        if (_quitting) return;
#if DEBUG
        // Dev-Builds: X = wirklich beenden, damit Iteration nicht im Tray stecken bleibt
        Logger.Info("Window X clicked in DEBUG build → quitting instead of hiding");
        DoQuit();
        return;
#else
        e.Handled = true;
        AppWindow.Hide();
#endif
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Logger.FilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Log öffnen fehlgeschlagen: " + ex.Message;
        }
    }

    private async void OnTestServer(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Teste Server…";
        try
        {
            var s = App.Current.Settings;
            // wss://host/ws/transcribe → https://host/api/health
            var uri = new Uri(s.ServerUrl);
            var scheme = uri.Scheme == "wss" ? "https" : "http";
            var healthUrl = $"{scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/api/health";
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(healthUrl);
            Logger.Info($"Health: {json}");
            StatusText.Text = "Server: " + json;
        }
        catch (Exception ex)
        {
            Logger.Error("Health check failed", ex);
            StatusText.Text = string.Format(App.Current.Loc.T("Status.ErrorFmt"), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        UpdateStatus.Text = "Prüfe…";
        await App.Current.Updates.CheckAsync();
    }

    private async void OnApplyUpdate(object sender, RoutedEventArgs e)
    {
        ApplyUpdateButton.IsEnabled = false;
        await App.Current.Updates.ApplyAndRestartAsync();
    }

    private void ShowUpdateStatus(UpdateService.Status s)
    {
        UpdateStatus.Text = s.Message ?? "";
        ApplyUpdateButton.Visibility = s.IsUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        ApplyUpdateButton.IsEnabled = s.IsUpdateAvailable;
    }

    private async void OnTrayToggle(object sender, RoutedEventArgs e)
        => await App.Current.Coordinator.ToggleAsync();

    private void OnUiLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageCombo.SelectedItem is not ComboBoxItem item) return;
        var code = (string)item.Tag;
        if (App.Current.Settings.UiLanguage == code) return;
        App.Current.Settings.UiLanguage = code;
        App.Current.SaveSettings();
        App.Current.Loc.Load(code); // löst LanguageChanged → ApplyLocalization
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        var mode = (string)item.Tag;
        if (App.Current.Settings.Theme == mode) return;
        App.Current.Settings.Theme = mode;
        App.Current.SaveSettings();
        Themes.ThemeManager.Apply(mode);
    }

    private void ApplyLocalization()
    {
        var L = App.Current.Loc;
        Title = L.T("Window.Title");
        WindowTitleLabel.Text = L.T("Window.Title");
        OnlinePillText.Text = L.T("Status.Online");

        // NavView
        ((NavigationViewItem)Nav.MenuItems[0]).Content = L.T("Nav.Transcript");
        ((NavigationViewItem)Nav.MenuItems[1]).Content = L.T("Nav.Profile");
        ((NavigationViewItem)Nav.MenuItems[2]).Content = L.T("Nav.Settings");
        NavWhatsNewItem.Content = L.T("Nav.WhatsNew");
        ToolTipService.SetToolTip(NavWhatsNewItem, L.T("Nav.WhatsNew"));
        NavAboutItem.Content = L.T("Nav.About");
        ToolTipService.SetToolTip(NavAboutItem, L.T("Nav.About"));

        // WhatsNew + About statische Texte
        WhatsNewSectionLabel.Text = L.T("WhatsNew.Section");
        WhatsNewTitle.Text = L.T("WhatsNew.Title");
        WhatsNewSubtitle.Text = string.Format(L.T("WhatsNew.SubtitleFmt"), WhatsNewService.CurrentVersion());
        RenderAbout();

        // Tray
        try
        {
            TrayIcon.ToolTipText = App.Current.Coordinator.IsRecording
                ? L.T("Tray.TooltipRecording") : L.T("Tray.Tooltip");
            TrayToggleItem.Text = L.T("Tray.Toggle");
            TrayShowItem.Text = L.T("Tray.ShowWindow");
            TrayQuitItem.Text = L.T("Tray.Quit");
        }
        catch { }

        // ----- Home -----
        TranscriptBox.PlaceholderText = L.T("Home.Placeholder");
        HotkeyEnabledLabel.Text = L.T("Home.HotkeyEnabled");
        HomeLanguageLabel.Text = L.T("Home.SpeechLanguage");
        if (HomeLanguageCombo.Items.Count >= 3)
        {
            ((ComboBoxItem)HomeLanguageCombo.Items[0]).Content = L.T("Settings.Lang.Auto");
            ((ComboBoxItem)HomeLanguageCombo.Items[1]).Content = L.T("Settings.Lang.De");
            ((ComboBoxItem)HomeLanguageCombo.Items[2]).Content = L.T("Settings.Lang.En");
        }
        HomeOutputModeLabel.Text = L.T("Home.OutputMode");
        if (HomeOutputModeCombo.Items.Count >= 5)
        {
            ((ComboBoxItem)HomeOutputModeCombo.Items[0]).Content = L.T("OutputMode.Clipboard");
            ((ComboBoxItem)HomeOutputModeCombo.Items[1]).Content = L.T("OutputMode.Paste");
            ((ComboBoxItem)HomeOutputModeCombo.Items[2]).Content = L.T("OutputMode.DirectInsert");
            ((ComboBoxItem)HomeOutputModeCombo.Items[3]).Content = L.T("OutputMode.Type");
            ((ComboBoxItem)HomeOutputModeCombo.Items[4]).Content = L.T("Settings.OutputMode.Notification");
        }
        TranscriptSectionLabel.Text = L.T("Home.Title");
        UploadHeadline.Text = L.T("Upload.Headline");
        UploadSubline.Text = L.T("Upload.Subline");
        UploadBrowseButton.Content = L.T("Upload.Btn.Browse");
        HistoryLabel.Text = L.T("Home.History.Label");
        HistoryFilter.PlaceholderText = L.T("Home.History.FilterPlaceholder");
        PromptPreviewLabel.Text = L.T("Home.Prompt.Label");
        if (string.IsNullOrWhiteSpace(PromptPreviewText.Text))
            PromptPreviewText.Text = L.T("Home.Prompt.Empty");
        UpdateHeroState();
        UpdateTranscriptStats();

        // ----- Profile -----
        ProfileSectionLabel.Text = L.T("Profile.Section");
        ProfileTitle.Text = L.T("Profile.Title");
        ProfileLoadButton.Content = L.T("Profile.Btn.Reload");
        ProfileSaveButton.Content = L.T("Profile.Btn.Save");
        StatActiveModelLabel.Text = L.T("Profile.Stat.ActiveModel");
        StatTempLabel.Text = L.T("Profile.Stat.Temperature");
        StatPromptLabel.Text = L.T("Profile.Stat.Prompt");
        StatApiKeyLabel.Text = L.T("Profile.Stat.ApiKey");
        ProfileModelLabel.Text = L.T("Profile.Model");
        ProfilePromptLabel.Text = L.T("Profile.Prompt");
        ProfilePromptHint.Text = L.T("Profile.Prompt.Hint");
        ProfilePromptDeLabel.Text = L.T("Profile.Prompt.De");
        ProfilePromptEnLabel.Text = L.T("Profile.Prompt.En");
        ProfilePromptAutoHint.Text = L.T("Profile.Prompt.AutoHint");
        ServerPromptDeBox.PlaceholderText = L.T("Profile.Prompt.PlaceholderDe");
        ServerPromptEnBox.PlaceholderText = L.T("Profile.Prompt.PlaceholderEn");
        ProfileTempHint.Text = L.T("Profile.TempHint");
        ServerTempLabel.Text = string.Format(L.T("Profile.TempFmt"), ServerTempSlider.Value);
        ModelsSectionLabel.Text = L.T("Models.Section");
        ModelsTitle.Text = L.T("Models.Title");
        HfSearchSectionLabel.Text = L.T("HfSearch.Section");
        HfSearchHint.Text = L.T("HfSearch.Hint");
        HfSearchBox.PlaceholderText = L.T("HfSearch.Placeholder");
        ModelsEmptyHint.Text = L.T("Models.EmptyHint");

        // ----- Settings: vertikale Sub-Nav -----
        SettingsSectionLabel.Text = L.T("Settings.Section");
        NavSettingsConnectionLabel.Text = L.T("Settings.Card.Connection");
        NavSettingsRecordingLabel.Text = L.T("Settings.Card.Recording");
        NavSettingsSoundsLabel.Text = L.T("Settings.Card.Sounds");
        NavSettingsSystemLabel.Text = L.T("Settings.Card.System");

        // Sub-Beschriftungen für Karteninhalte
        HotkeyHintLabel.Text = L.T("Settings.HotkeyHint");
        ToastCheckLabel.Text = L.T("Settings.ToastOnResult");
        AutoStartLabel.Text = L.T("Settings.AutoStart");
        HudEnabledLabel.Text = L.T("Settings.HudShow");
        HudEnabledSubtitle.Text = L.T("Settings.HudSubtitle");
        DiagnosticsLabel.Text = L.T("Settings.Diagnostics");

        // Sound-Probehören-Buttons
        SoundStartPreviewLabel.Text = L.T("Settings.Sound.Preview");
        SoundStopPreviewLabel.Text = L.T("Settings.Sound.Preview");
        SoundDonePreviewLabel.Text = L.T("Settings.Sound.Preview");
        SoundErrorPreviewLabel.Text = L.T("Settings.Sound.Preview");

        // Aktiven Tab wiederherstellen
        ApplySettingsTab(_activeSettingsTab);
        ServerUrlLabel.Text = L.T("Settings.ServerUrl");
        ServerUrlBox.PlaceholderText = "wss://va.example.com/ws/transcribe";
        ApiKeyLabel.Text = L.T("Settings.ApiKey");
        LanguageLabel.Text = L.T("Settings.Language");
        if (LanguageCombo.Items.Count >= 3)
        {
            ((ComboBoxItem)LanguageCombo.Items[0]).Content = L.T("Settings.Lang.Auto");
            ((ComboBoxItem)LanguageCombo.Items[1]).Content = L.T("Settings.Lang.De");
            ((ComboBoxItem)LanguageCombo.Items[2]).Content = L.T("Settings.Lang.En");
        }
        LanguageAutoHint.Text = L.T("Settings.Lang.AutoHint");
        MicLabel.Text = L.T("Settings.Mic");
        HotkeyLabel.Text = L.T("Settings.Hotkey");
        HotkeyButton.Content = L.T("Settings.Hotkey.Record");
        OutputModeLabel.Text = L.T("Settings.OutputMode");
        if (OutputModeCombo.Items.Count >= 5)
        {
            ((ComboBoxItem)OutputModeCombo.Items[0]).Content = L.T("OutputMode.Clipboard");
            ((ComboBoxItem)OutputModeCombo.Items[1]).Content = L.T("OutputMode.Paste");
            ((ComboBoxItem)OutputModeCombo.Items[2]).Content = L.T("OutputMode.DirectInsert");
            ((ComboBoxItem)OutputModeCombo.Items[3]).Content = L.T("OutputMode.Type");
            ((ComboBoxItem)OutputModeCombo.Items[4]).Content = L.T("Settings.OutputMode.Notification");
        }
        TypingSpeedLabel.Text = L.T("Settings.TypingSpeed");
        TypingSpeedHint.Text = L.T("Settings.TypingSpeed.Hint");
        HomeTypingSpeedLabel.Text = L.T("Home.TypingSpeed");
        UpdateTypingSpeedValueLabels((int)TypingSpeedSlider.Value);

        // ----- Sounds -----
        SoundOnStartLabel.Text = L.T("Settings.Sound.OnStart");
        SoundOnStopLabel.Text = L.T("Settings.Sound.OnStop");
        SoundOnDoneLabel.Text = L.T("Settings.Sound.OnDone");
        SoundOnErrorLabel.Text = L.T("Settings.Sound.OnError");
        VolumeLabel.Text = L.T("Settings.Volume");
        VolumeValueLabel.Text = $"{(int)VolumeSlider.Value}%";

        // Sound-Combos: Display-Strings für SoundChoice neu setzen
        RefreshSoundCombo(SoundStartCombo);
        RefreshSoundCombo(SoundStopCombo);
        RefreshSoundCombo(SoundDoneCombo);
        RefreshSoundCombo(SoundErrorCombo);

        // ----- UI-Sprache-Picker -----
        if (UiLanguageCombo.Items.Count == 0)
        {
            foreach (var (code, display) in LocalizationService.AvailableLanguages)
                UiLanguageCombo.Items.Add(new ComboBoxItem { Content = display, Tag = code });
        }
        foreach (ComboBoxItem ci in UiLanguageCombo.Items)
        {
            if ((string)ci.Tag == App.Current.Settings.UiLanguage) { UiLanguageCombo.SelectedItem = ci; break; }
        }
        UiLanguageLabel.Text = L.T("Settings.UiLanguage");

        // ----- Theme-Picker -----
        ThemeLabel.Text = L.T("Settings.Theme");
        if (ThemeCombo.Items.Count >= 2)
        {
            ((ComboBoxItem)ThemeCombo.Items[0]).Content = L.T("Settings.Theme.Dark");
            ((ComboBoxItem)ThemeCombo.Items[1]).Content = L.T("Settings.Theme.Light");
        }
        foreach (ComboBoxItem ci in ThemeCombo.Items)
        {
            if ((string)ci.Tag == App.Current.Settings.Theme) { ThemeCombo.SelectedItem = ci; break; }
        }

        HudPreviewButton.Content = L.T("Settings.HudPreview");

        // HUD-Positions
        if (HudPositionCombo.Items.Count >= 9)
        {
            var keys = new[] { "Hud.TopLeft", "Hud.TopCenter", "Hud.TopRight",
                "Hud.MiddleLeft", "Hud.MiddleCenter", "Hud.MiddleRight",
                "Hud.BottomLeft", "Hud.BottomCenter", "Hud.BottomRight" };
            for (var i = 0; i < keys.Length; i++)
                ((ComboBoxItem)HudPositionCombo.Items[i]).Content = L.T(keys[i]);
        }

        // Footer-Buttons
        BtnMinimize.Content = L.T("Btn.Minimize");
        BtnOpenLog.Content = L.T("Btn.OpenLog");
        BtnTestServer.Content = L.T("Btn.TestServer");
        BtnCheckUpdates.Content = L.T("Btn.CheckUpdates");
        ApplyUpdateButton.Content = L.T("Btn.InstallUpdate");

        // Modell-Katalog neu rendern (Sprache der Buttons)
        if (ModelCatalogList?.Items.Count > 0) BuildCatalogList();
    }

    private static void RefreshSoundCombo(ComboBox combo)
    {
        // Display-Texte aktualisieren, ohne Auswahl zu verlieren
        foreach (var raw in combo.Items)
        {
            if (raw is ComboBoxItem item && item.Tag is SoundChoice sc)
                item.Content = SoundService.Display(sc);
        }
    }

    private void OnTrayShow(object sender, RoutedEventArgs e) => Activate();

    private void OnTrayQuit(object sender, RoutedEventArgs e) => DoQuit();

    private void DoQuit()
    {
        Logger.Info("Quit requested via tray menu");
        _quitting = true;
        try { App.Current.Hotkey.Dispose(); } catch { }
        try { App.Current.HudWindow?.Close(); } catch { }
        try { TrayIcon.Dispose(); } catch { }
        try { Application.Current.Exit(); } catch { }
        // Wenn Application.Exit nicht greift: Prozess hart killen.
        try
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    private void UpdateTrayState()
    {
        var iconPath = App.Current.Coordinator.IsRecording ? "Assets/TrayRecording.ico" : "Assets/TrayIdle.ico";
        TrayIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri($"ms-appx:///{iconPath}"));
        TrayIcon.ToolTipText = App.Current.Coordinator.IsRecording
            ? App.Current.Loc.T("Tray.TooltipRecording")
            : App.Current.Loc.T("Tray.Tooltip");
    }

    private sealed record MicEntry(int Number, string Name)
    {
        public override string ToString() => Name;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static (int w, int h) ComputeMinSize(IntPtr hwnd)
    {
        const int MinEffW = 960;
        const int MinEffH = 900;
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        return ((int)Math.Round(MinEffW * scale), (int)Math.Round(MinEffH * scale));
    }

    private void ApplyInitialGeometry(IntPtr hwnd)
    {
        var s = App.Current.Settings;
        var (minW, minH) = ComputeMinSize(hwnd);

        int width = s.WindowWidth ?? minW;
        int height = s.WindowHeight ?? minH;
        if (width < minW) width = minW;
        if (height < minH) height = minH;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        if (s.WindowX is int x && s.WindowY is int y)
        {
            try { AppWindow.Move(new Windows.Graphics.PointInt32(x, y)); }
            catch { /* Off-Screen oder Monitor weg → Windows zentriert default */ }
        }
    }

    private void PersistWindowGeometry()
    {
        if (_quitting) return;
        try
        {
            var s = App.Current.Settings;
            s.WindowWidth = AppWindow.Size.Width;
            s.WindowHeight = AppWindow.Size.Height;
            s.WindowX = AppWindow.Position.X;
            s.WindowY = AppWindow.Position.Y;
            App.Current.SettingsStore.Save(s);
        }
        catch (Exception ex) { Logger.Warn("PersistWindowGeometry: " + ex.Message); }
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?>? _exec;
        private readonly Func<object?, Task>? _execAsync;
        public RelayCommand(Action<object?> exec) => _exec = exec;
        public RelayCommand(Func<object?, Task> exec) => _execAsync = exec;
        public bool CanExecute(object? p) => true;
        public void Execute(object? p)
        {
            try
            {
                if (_exec is not null) _exec(p);
                else if (_execAsync is not null) _ = _execAsync(p);
            }
            catch (Exception ex) { Logger.Error("RelayCommand", ex); }
        }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
