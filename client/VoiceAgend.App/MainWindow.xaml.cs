using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1440));
        App.Current.Loc.LanguageChanged += () => DispatcherQueue.TryEnqueue(ApplyLocalization);
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
            DispatcherQueue.TryEnqueue(UpdateTrayState);
        App.Current.Coordinator.TranscriptReceived += t =>
            DispatcherQueue.TryEnqueue(() => TranscriptBox.Text = t);
        App.Current.Updates.StatusChanged += st =>
            DispatcherQueue.TryEnqueue(() => ShowUpdateStatus(st));

        ShowWindowCommand = new RelayCommand(_ => Activate());
        ToggleCommand = new RelayCommand(async _ => await App.Current.Coordinator.ToggleAsync());
        QuitCommand = new RelayCommand(_ => DoQuit());

        // Capture key presses on the window when in hotkey-record mode
        if (Content is FrameworkElement fe)
            fe.KeyDown += OnRootKeyDown;
    }

    private void LoadIntoUi()
    {
        _suppressAutoSave = true;
        HotkeyEnabledToggle.IsOn = App.Current.Settings.HotkeyEnabled;
        _suppressAutoSave = false;
        ApplyLocalization();

        // Initial: Home-View aktiv
        if (Nav.SelectedItem == null)
            Nav.SelectedItem = Nav.MenuItems[0];

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

        ToastCheck.IsChecked = s.ShowToastOnResult;

        // Sound-Combos befüllen + Auswahl setzen
        InitSoundCombo(SoundStartCombo, s.SoundOnStart);
        InitSoundCombo(SoundStopCombo, s.SoundOnStop);
        InitSoundCombo(SoundDoneCombo, s.SoundOnDone);
        InitSoundCombo(SoundErrorCombo, s.SoundOnError);
        VolumeSlider.Value = s.SoundVolume;
        VolumeLabel.Text = $"Lautstärke: {s.SoundVolume}%";

        AutoStartCheck.IsChecked = App.Current.AutoStart.IsEnabled;
        HudEnabledCheck.IsChecked = s.HudEnabled;
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
        var enable = AutoStartCheck.IsChecked == true;
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
        if (tag == "profile") _ = LoadProfileAsync();
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
            ProfileStatus.Text = "Erst API-Key in Einstellungen setzen.";
            return;
        }
        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            ProfileStatus.Text = "Lade…";

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

            if (string.IsNullOrEmpty(p.Model))
                ServerModelCombo.SelectedIndex = 0;
            else
            {
                var idx = ServerModelCombo.Items.IndexOf(p.Model);
                ServerModelCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            ServerPromptBox.Text = p.Prompt ?? "";
            ServerTempSlider.Value = p.Temperature;
            ServerTempLabel.Text = string.Format(App.Current.Loc.T("Profile.TempFmt"), p.Temperature);

            BuildCatalogList();

            ProfileStatus.Text = $"{models.Count} ✓";
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
        foreach (var entry in ModelCatalog.All)
        {
            ModelCatalogList.Items.Add(BuildCatalogRow(entry));
        }
    }

    private FrameworkElement BuildCatalogRow(ModelCatalog.Entry entry)
    {
        var L = App.Current.Loc;
        var installed = _installedModels.Contains(entry.Id);

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = $"{entry.Label}  ·  {entry.SizeApprox}",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        info.Children.Add(new TextBlock
        {
            Text = entry.Hint, TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 12,
        });
        info.Children.Add(new TextBlock
        {
            Text = entry.Id, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 11,
        });
        Grid.SetColumn(info, 0);

        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (installed)
        {
            actionPanel.Children.Add(new TextBlock
            {
                Text = L.T("Models.Installed"), VerticalAlignment = VerticalAlignment.Center,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen),
            });
        }
        else
        {
            var progress = new ProgressRing { IsActive = false, Width = 18, Height = 18 };
            var btn = new Button { Content = L.T("Models.Btn.Install") };
            btn.Click += async (_, _) => await InstallModelAsync(entry, btn, progress);
            actionPanel.Children.Add(progress);
            actionPanel.Children.Add(btn);
        }
        Grid.SetColumn(actionPanel, 1);

        grid.Children.Add(info);
        grid.Children.Add(actionPanel);
        return grid;
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

    private async void OnProfileSave(object sender, RoutedEventArgs e)
    {
        var s = App.Current.Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey))
        {
            ProfileStatus.Text = "Erst API-Key in Einstellungen setzen.";
            return;
        }
        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            string? model = null;
            if (ServerModelCombo.SelectedItem is string m && ServerModelCombo.SelectedIndex > 0)
                model = m;
            var prompt = ServerPromptBox.Text;
            var temp = ServerTempSlider.Value;
            await App.Current.ServerApi.UpdateProfileAsync(baseUrl, s.ApiKey, model ?? "", prompt, temp);
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

    private void AutoSave()
    {
        if (_suppressAutoSave) return;
        var s = App.Current.Settings;
        s.ServerUrl = ServerUrlBox.Text.Trim();
        s.ApiKey = ApiKeyBox.Password.Trim();
        s.Language = ReadLanguageSelection();

        if (MicCombo.SelectedItem is MicEntry mic)
        {
            s.MicDeviceNumber = mic.Number;
            s.MicDeviceName = mic.Name;
        }

        if (OutputModeCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag &&
            Enum.TryParse<OutputMode>(tag, out var mode))
            s.OutputMode = mode;

        s.ShowToastOnResult = ToastCheck.IsChecked == true;

        s.SoundOnStart = ReadSoundCombo(SoundStartCombo);
        s.SoundOnStop = ReadSoundCombo(SoundStopCombo);
        s.SoundOnDone = ReadSoundCombo(SoundDoneCombo);
        s.SoundOnError = ReadSoundCombo(SoundErrorCombo);
        s.SoundVolume = (int)VolumeSlider.Value;

        s.HudEnabled = HudEnabledCheck.IsChecked == true;
        if (HudPositionCombo.SelectedItem is ComboBoxItem hci && hci.Tag is string htag &&
            Enum.TryParse<HudPosition>(htag, out var hpos))
            s.HudPosition = hpos;

        App.Current.SaveSettings();
        App.Current.Hud?.ApplyPosition();
        StatusText.Text = string.Format(App.Current.Loc.T("Status.SavedFmt"), DateTime.Now);
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e) => AutoSave();
    private void OnFieldChanged(object sender, object e) => AutoSave();

    private void SetLanguageSelection(string lang)
    {
        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if ((string)item.Tag == (lang ?? ""))
            {
                LanguageCombo.SelectedItem = item;
                return;
            }
        }
        LanguageCombo.SelectedIndex = 0;
    }

    private string ReadLanguageSelection() =>
        LanguageCombo.SelectedItem is ComboBoxItem ci ? (string)ci.Tag : "";

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
        VolumeLabel.Text = string.Format(App.Current.Loc.T("Settings.VolumeFmt"), (int)VolumeSlider.Value);
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
        e.Handled = true;
        AppWindow.Hide();
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

    private void ApplyLocalization()
    {
        var L = App.Current.Loc;
        Title = L.T("Window.Title");

        // NavView
        ((NavigationViewItem)Nav.MenuItems[0]).Content = L.T("Nav.Transcript");
        ((NavigationViewItem)Nav.MenuItems[1]).Content = L.T("Nav.Profile");
        ((NavigationViewItem)Nav.MenuItems[2]).Content = L.T("Nav.Settings");

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
        HomeTitle.Text = L.T("Home.Title");
        TranscriptBox.PlaceholderText = L.T("Home.Placeholder");
        BtnCopyTranscript.Content = L.T("Home.Btn.Copy");
        BtnClearTranscript.Content = L.T("Home.Btn.Clear");
        BtnHomeToggle.Content = L.T("Home.Btn.Toggle");
        HotkeyEnabledLabel.Text = L.T("Home.HotkeyEnabled");

        // ----- Profile -----
        ProfileTitle.Text = L.T("Profile.Title");
        ProfileDescription.Text = L.T("Profile.Description");
        ProfileLoadButton.Content = L.T("Profile.Btn.Reload");
        ProfileSaveButton.Content = L.T("Profile.Btn.Save");
        ProfileModelLabel.Text = L.T("Profile.Model");
        ProfilePromptLabel.Text = L.T("Profile.Prompt");
        ServerPromptBox.PlaceholderText = L.T("Profile.Prompt.Placeholder");
        ProfileTempHint.Text = L.T("Profile.TempHint");
        ServerTempLabel.Text = string.Format(L.T("Profile.TempFmt"), ServerTempSlider.Value);
        ModelsTitle.Text = L.T("Models.Title");
        ModelsDescription.Text = L.T("Models.Description");

        // ----- Settings -----
        SettingsTitle.Text = L.T("Settings.Title");
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
        MicLabel.Text = L.T("Settings.Mic");
        HotkeyLabel.Text = L.T("Settings.Hotkey");
        HotkeyButton.Content = L.T("Settings.Hotkey.Record");
        OutputModeLabel.Text = L.T("Settings.OutputMode");
        if (OutputModeCombo.Items.Count >= 3)
        {
            ((ComboBoxItem)OutputModeCombo.Items[0]).Content = L.T("OutputMode.Clipboard");
            ((ComboBoxItem)OutputModeCombo.Items[1]).Content = L.T("OutputMode.Type");
            ((ComboBoxItem)OutputModeCombo.Items[2]).Content = L.T("Settings.OutputMode.Notification");
        }
        ToastCheck.Content = L.T("Settings.ToastOnResult");

        // ----- Sounds -----
        SoundsTitle.Text = L.T("Settings.Sounds");
        SoundOnStartLabel.Text = L.T("Settings.Sound.OnStart");
        SoundOnStopLabel.Text = L.T("Settings.Sound.OnStop");
        SoundOnDoneLabel.Text = L.T("Settings.Sound.OnDone");
        SoundOnErrorLabel.Text = L.T("Settings.Sound.OnError");
        SoundStartPreview.Content = L.T("Settings.Sound.Preview");
        SoundStopPreview.Content = L.T("Settings.Sound.Preview");
        SoundDonePreview.Content = L.T("Settings.Sound.Preview");
        SoundErrorPreview.Content = L.T("Settings.Sound.Preview");
        VolumeLabel.Text = string.Format(L.T("Settings.VolumeFmt"), (int)VolumeSlider.Value);

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

        AutoStartCheck.Content = L.T("Settings.AutoStart");
        HudEnabledCheck.Content = L.T("Settings.HudShow");
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
