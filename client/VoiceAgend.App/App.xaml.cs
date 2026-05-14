using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using VoiceAgend.App.Models;
using VoiceAgend.App.Services;

namespace VoiceAgend.App;

public partial class App : Application
{
    public static App Current => (App)Application.Current;

    public SettingsStore SettingsStore { get; } = new();
    public AppSettings Settings { get; private set; } = new();
    public AudioCaptureService Audio { get; } = new();
    public TranscriptionClient Client { get; } = new();
    public OutputService Output { get; } = new();
    public SoundService Sounds { get; } = new();
    public ServerApiClient ServerApi { get; } = new();
    public HotkeyManager Hotkey { get; } = new();
    public UpdateService Updates { get; } = new();
    public AutoStartService AutoStart { get; } = new();
    public LocalizationService Loc { get; } = new();
    public RecordingCoordinator Coordinator { get; private set; } = null!;

    public MainWindow? MainWindow { get; private set; }
    public HudWindow? HudWindow { get; private set; }
    public HudController? Hud { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Logger.Info("===== VoiceAgend.App started =====");
        CrashHandler.Install();
        UnhandledException += (_, e) =>
        {
            CrashHandler.Handle("App.UnhandledException", e.Exception);
            e.Handled = true;
        };

        Settings = SettingsStore.Load();
        Loc.Load(Settings.UiLanguage);
        Coordinator = new RecordingCoordinator(Audio, Client, Output, Sounds, () => Settings);

        MainWindow = new MainWindow();
        Output.AttachUiThread(MainWindow.DispatcherQueue);

        HudWindow = new HudWindow();
        Hud = new HudController(HudWindow, () => Settings);
        Coordinator.StatusChanged += s => Hud.OnStatus(s);

        Hotkey.Pressed += OnHotkeyPressed;
        ApplyHotkey();

        // Fenster immer aktivieren, damit Tray-Icon initialisiert wird.
        // User kann via "In Tray minimieren" verstecken.
        MainWindow.Activate();
        Logger.Info($"Hotkey registered: {Settings.HotkeyDisplay()}");

        // Toast-Activation registrieren (Windows ruft uns zurück, wenn Buttons im Toast geklickt werden)
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex) { Logger.Warn("NotificationManager register: " + ex.Message); }

        // Bei jedem Update-Check: wenn Update verfügbar, prominent als Toast anzeigen
        Updates.StatusChanged += s =>
        {
            if (s.IsUpdateAvailable)
                _ = ShowUpdateToastAsync(s.AvailableVersion ?? "");
        };

        // Stiller Update-Check beim Start (3 s warten, damit UI nicht blockt)
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            await Updates.CheckAsync();
        });
    }

    public void ApplyHotkey()
    {
        if (Settings.HotkeyEnabled)
            Hotkey.Register(Settings.HotkeyModifiers, Settings.HotkeyVirtualKey);
        else
            Hotkey.Unregister();
    }

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        ApplyHotkey();
    }

    private async void OnHotkeyPressed()
    {
        try { await Coordinator.ToggleAsync(); }
        catch (Exception ex) { CrashHandler.Handle("Hotkey-Pressed", ex); }
    }

    private bool _toastShownForVersion;
    private string? _lastToastVersion;

    private Task ShowUpdateToastAsync(string version)
    {
        // Pro Version nur einmal pro App-Lauf toasten
        if (_lastToastVersion == version) return Task.CompletedTask;
        _lastToastVersion = version;

        try
        {
            var L = Loc;
            var titleKey = L.T("Update.Toast.Title");
            var bodyKey = string.Format(L.T("Update.Toast.BodyFmt"), version);
            var installBtn = L.T("Btn.InstallUpdate");
            var showBtn = L.T("Tray.ShowWindow");

            var n = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText(titleKey)
                .AddText(bodyKey)
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton(installBtn)
                    .AddArgument("action", "install"))
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton(showBtn)
                    .AddArgument("action", "show"))
                .SetTag("update")
                .BuildNotification();
            // Default-Klick auf den Toast-Body öffnet das Fenster:
            n.Tag = "update";
            // Set top-level argument so plain body click also has an action
            AppNotificationManager.Default.Show(n);
            _toastShownForVersion = true;
        }
        catch (Exception ex) { Logger.Warn("Update toast: " + ex.Message); }
        return Task.CompletedTask;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            args.Arguments.TryGetValue("action", out var action);
            if (string.IsNullOrEmpty(action)) action = "show";

            var dq = MainWindow?.DispatcherQueue;
            dq?.TryEnqueue(async () =>
            {
                MainWindow?.Activate();
                if (action == "install")
                {
                    await Updates.ApplyAndRestartAsync();
                }
            });
        }
        catch (Exception ex) { Logger.Error("Notification invoked", ex); }
    }
}
