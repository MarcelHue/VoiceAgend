using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using System.Windows.Input;
using Windows.System;
using VoiceAgend.App.Models;
using VoiceAgend.App.Services;
// for HudState reference


namespace VoiceAgend.App;

public sealed partial class MainWindow : Window
{
    private bool _recordingHotkey;

    public ICommand ShowWindowCommand { get; }

    private bool _suppressAutoSave;
    private bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        Title = "VoiceAgend";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1440));
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

        // Capture key presses on the window when in hotkey-record mode
        if (Content is FrameworkElement fe)
            fe.KeyDown += OnRootKeyDown;
    }

    private void LoadIntoUi()
    {
        // Initial: Home-View aktiv
        if (Nav.SelectedItem == null)
            Nav.SelectedItem = Nav.MenuItems[0];

        // Letztes Transkript einblenden, falls vorhanden
        if (!string.IsNullOrEmpty(App.Current.Coordinator.LastTranscript))
            TranscriptBox.Text = App.Current.Coordinator.LastTranscript;

        var s = App.Current.Settings;
        ServerUrlBox.Text = s.ServerUrl;
        ApiKeyBox.Password = s.ApiKey;
        LanguageBox.Text = s.Language;

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
        ServerTempLabel.Text = $"Temperature: {ServerTempSlider.Value:F2}";
    }

    private async void OnProfileLoad(object sender, RoutedEventArgs e) => await LoadProfileAsync(force: true);

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
            ServerModelCombo.Items.Clear();
            ServerModelCombo.Items.Add("(Server-Default)");
            foreach (var m in models) ServerModelCombo.Items.Add(m.Id);

            // Profil
            var p = await App.Current.ServerApi.GetProfileAsync(baseUrl, s.ApiKey);
            ProfileApiKeyLabel.Text = $"API-Key: {p.ApiKeyName} (#{p.ApiKeyId})";

            if (string.IsNullOrEmpty(p.Model))
                ServerModelCombo.SelectedIndex = 0;
            else
            {
                var idx = ServerModelCombo.Items.IndexOf(p.Model);
                ServerModelCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            ServerPromptBox.Text = p.Prompt ?? "";
            ServerTempSlider.Value = p.Temperature;
            ServerTempLabel.Text = $"Temperature: {p.Temperature:F2}";

            ProfileStatus.Text = $"Geladen ({models.Count} Modelle).";
        }
        catch (Exception ex)
        {
            Logger.Error("Profile load", ex);
            ProfileStatus.Text = $"Fehler: {ex.Message}";
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
            ProfileStatus.Text = $"Gespeichert ({DateTime.Now:HH:mm:ss}).";
        }
        catch (Exception ex)
        {
            Logger.Error("Profile save", ex);
            ProfileStatus.Text = $"Fehler: {ex.Message}";
        }
    }

    private void OnCopyTranscript(object sender, RoutedEventArgs e)
    {
        var text = TranscriptBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            HomeStatus.Text = "Kein Text zum Kopieren.";
            return;
        }
        App.Current.Output.CopyToClipboard(text);
        HomeStatus.Text = $"Kopiert ({text.Length} Zeichen).";
    }

    private void OnClearTranscript(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Text = "";
        HomeStatus.Text = "Geleert.";
    }

    private void AutoSave()
    {
        if (_suppressAutoSave) return;
        var s = App.Current.Settings;
        s.ServerUrl = ServerUrlBox.Text.Trim();
        s.ApiKey = ApiKeyBox.Password.Trim();
        s.Language = LanguageBox.Text.Trim();

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
        StatusText.Text = $"Gespeichert ({DateTime.Now:HH:mm:ss}).";
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e) => AutoSave();
    private void OnFieldChanged(object sender, object e) => AutoSave();

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
        VolumeLabel.Text = $"Lautstärke: {(int)VolumeSlider.Value}%";
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
        HotkeyButton.Content = "Drücke jetzt eine Taste…";
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
        HotkeyButton.Content = "Hotkey aufzeichnen…";
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
            StatusText.Text = $"Server-Test Fehler: {ex.GetType().Name}: {ex.Message}";
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

    private void OnTrayShow(object sender, RoutedEventArgs e) => Activate();

    private void OnTrayQuit(object sender, RoutedEventArgs e)
    {
        _quitting = true;
        try { App.Current.Hotkey.Dispose(); } catch { }
        try { App.Current.HudWindow?.Close(); } catch { }
        try { TrayIcon.Dispose(); } catch { }
        try { Application.Current.Exit(); } catch { }
        // Hard-Exit als Fallback — WinUI-3-Unpackaged hängt sonst gerne an
        // Background-Threads (Hotkey-Loop, NAudio, Velopack).
        Environment.Exit(0);
    }

    private void UpdateTrayState()
    {
        var iconPath = App.Current.Coordinator.IsRecording ? "Assets/TrayRecording.ico" : "Assets/TrayIdle.ico";
        TrayIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri($"ms-appx:///{iconPath}"));
        TrayIcon.ToolTipText = App.Current.Coordinator.IsRecording ? "VoiceAgend (Aufnahme)" : "VoiceAgend";
    }

    private sealed record MicEntry(int Number, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _exec;
        public RelayCommand(Action<object?> exec) => _exec = exec;
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _exec(p);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
