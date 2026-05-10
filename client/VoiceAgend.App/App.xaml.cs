using Microsoft.UI.Xaml;
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

        // Stiller Update-Check beim Start (3 s warten, damit UI nicht blockt)
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            await Updates.CheckAsync();
        });
    }

    public void ApplyHotkey()
    {
        Hotkey.Register(Settings.HotkeyModifiers, Settings.HotkeyVirtualKey);
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
}
