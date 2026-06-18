using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
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
    public HuggingFaceSearchClient HfSearch { get; } = new();
    public HotkeyManager Hotkey { get; } = new();
    public MouseWheelHook WheelHook { get; }
    public UpdateService Updates { get; } = new();
    public WhatsNewService WhatsNew { get; } = new();
    public ProfileSyncService ProfileSync { get; } = new();
    public AutoStartService AutoStart { get; } = new();
    public LocalizationService Loc { get; } = new();
    public HistoryService History { get; } = new();
    public RecordingCoordinator Coordinator { get; private set; } = null!;

    public MainWindow? MainWindow { get; private set; }
    public HudWindow? HudWindow { get; private set; }
    public HudController? Hud { get; private set; }

    /// <summary>Wird gefeuert, wenn die Erkennungssprache per Scroll-Geste geändert wurde
    /// (für Combo-Refresh im MainWindow).</summary>
    public event Action? RecognitionLanguageChanged;

    // Sprachwechsel-Geste: true, sobald während eines gehaltenen Hotkeys gescrollt wurde →
    // die laufende Aufnahme wird beim Loslassen verworfen statt hochgeladen.
    private bool _pendingDiscard;
    private DispatcherQueueTimer? _releasePoll;

    public App()
    {
        InitializeComponent();
        WheelHook = new MouseWheelHook(() => (Settings.HotkeyModifiers, Settings.HotkeyVirtualKey));
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
        Themes.ThemeManager.Apply(Settings.Theme);
        Coordinator = new RecordingCoordinator(Audio, Client, Output, Sounds, () => Settings);
        Coordinator.TranscriptReceived += text => History.Add(text, Settings.Language, 0, 0);

        MainWindow = new MainWindow();
        Output.AttachUiThread(MainWindow.DispatcherQueue);

        HudWindow = new HudWindow();
        Hud = new HudController(HudWindow, () => Settings);
        Coordinator.StatusChanged += s => Hud.OnStatus(s);

        Hotkey.Pressed += OnHotkeyPressed;
        ApplyHotkey();

        // Sprachwechsel-Geste: Mausrad bei gehaltenem Hotkey → durch Sprachen zyklen.
        WheelHook.Scrolled += OnHotkeyScroll;
        WheelHook.Start();

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

        // First-Run-Dialog oder Startup-Sync — beides nach erstem Activate(),
        // damit der ContentDialog einen gültigen XamlRoot bekommt.
        MainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            try { await HandleSetupAndSyncAsync(); }
            catch (Exception ex) { Logger.Warn("Setup/sync: " + ex.Message); }
        });

        // Update-Check: 3 s nach Start, danach alle 30 Minuten
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            while (true)
            {
                try { await Updates.CheckAsync(); }
                catch (Exception ex) { Logger.Warn("Periodic update check: " + ex.Message); }
                await Task.Delay(TimeSpan.FromMinutes(30));
            }
        });
    }

    private async Task HandleSetupAndSyncAsync()
    {
        // Erster Start ohne API-Key → Setup-Dialog zeigen, der Server-URL + Key abfragt
        // und gleich validiert. Wenn der User „Überspringen“ klickt, läuft die App
        // weiter, aber ohne Server-Sync — bis er manuell in Settings konfiguriert.
        var s = Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey) || string.IsNullOrWhiteSpace(s.ServerUrl))
        {
            if (MainWindow?.Content is FrameworkElement fe && fe.XamlRoot is not null)
            {
                var res = await Views.SetupDialog.ShowAsync(fe.XamlRoot);
                if (res is { Confirmed: true })
                {
                    s.ServerUrl = res.ServerUrl;
                    s.ApiKey = res.ApiKey;
                    SettingsStore.Save(s);
                    ApplyHotkey();
                    // Wenn Remote-Settings vorhanden → ziehen wir die direkt
                    if (res.RemoteSettingsAvailable)
                        await ProfileSync.SyncOnStartupAsync();
                    else
                        ProfileSync.SchedulePush(TimeSpan.FromMilliseconds(500));
                }
            }
        }
        else
        {
            // Normalfall: Bidirektionaler Sync auf Basis Timestamp.
            await ProfileSync.SyncOnStartupAsync();
        }
    }

    public void ApplyHotkey()
    {
        if (Settings.HotkeyEnabled)
            // MOD_NOREPEAT (0x4000): ohne das feuert RegisterHotKey beim Halten der Taste
            // Auto-Repeat-WM_HOTKEY und würde die Aufnahme wiederholt togglen. Für die
            // Halten+Scroll-Geste muss ein gehaltener Hotkey genau EIN Event auslösen.
            Hotkey.Register(Settings.HotkeyModifiers | 0x4000, Settings.HotkeyVirtualKey);
        else
            Hotkey.Unregister();
    }

    public void SaveSettings()
    {
        // Settings-Timestamp bumpen, damit der Sync später weiß, was neuer ist.
        Settings.SettingsUpdatedAtUtc = DateTime.UtcNow;
        SettingsStore.Save(Settings);
        ApplyHotkey();
        // Asynchron auf den Server pushen (debounced).
        ProfileSync.SchedulePush();
    }

    private async void OnHotkeyPressed()
    {
        try { await Coordinator.ToggleAsync(); }
        catch (Exception ex) { CrashHandler.Handle("Hotkey-Pressed", ex); }
    }

    /// <summary>
    /// Mausrad bei gehaltenem Aufnahme-Hotkey: durch [Auto, de, en, …EnabledLanguages] zyklen.
    /// Läuft auf dem Maus-Hook-Thread → auf den UI-Thread dispatchen.
    /// </summary>
    private void OnHotkeyScroll(int dir)
    {
        var dq = MainWindow?.DispatcherQueue;
        if (dq is null) return;
        dq.TryEnqueue(() =>
        {
            // Zyklusliste: Auto + de + en + aktivierte Zusatzsprachen (dedupliziert).
            var cycle = new List<string> { "", "de", "en" };
            foreach (var c in Settings.EnabledLanguages)
            {
                var lc = (c ?? "").ToLowerInvariant();
                if (lc is "" or "de" or "en") continue;
                if (!cycle.Contains(lc)) cycle.Add(lc);
            }

            var idx = cycle.IndexOf((Settings.Language ?? "").ToLowerInvariant());
            if (idx < 0) idx = 0;
            idx = ((idx + dir) % cycle.Count + cycle.Count) % cycle.Count;
            Settings.Language = cycle[idx];

            // HUD-Live-Anzeige (force-show, auch wenn HudEnabled aus ist).
            var disp = string.IsNullOrEmpty(Settings.Language)
                ? Loc.T("Settings.Lang.Auto")
                : LanguageNames.Display(Settings.Language, Settings.UiLanguage);
            Hud?.ShowLanguage(disp);

            // Combos im Fenster spiegeln.
            RecognitionLanguageChanged?.Invoke();

            // Die durch den Tastendruck gestartete Aufnahme soll verworfen werden. Unbedingt
            // setzen (nicht nur bei IsRecording) — falls der Scroll knapp vor dem Audio-Start
            // eintrifft, greift Cancel() beim Loslassen trotzdem; ohne Aufnahme ist es ein No-op.
            _pendingDiscard = true;

            StartReleasePoller(dq);
        });
    }

    private void StartReleasePoller(DispatcherQueue dq)
    {
        if (_releasePoll is not null) return;
        _releasePoll = dq.CreateTimer();
        _releasePoll.Interval = TimeSpan.FromMilliseconds(40);
        _releasePoll.Tick += (t, _) =>
        {
            // Warten, bis die Haupttaste des Hotkeys losgelassen wird.
            if (MouseWheelHook.IsKeyDown(Settings.HotkeyVirtualKey)) return;
            t.Stop();
            _releasePoll = null;
            OnGestureReleased();
        };
        _releasePoll.Start();
    }

    private void OnGestureReleased()
    {
        // Gewählte Sprache dauerhaft sichern (einmalig, nicht bei jedem Scroll-Tick).
        SaveSettings();

        if (_pendingDiscard)
        {
            _pendingDiscard = false;
            Coordinator.Cancel(); // Audio verwerfen, kein Upload
        }

        // HUD nach kurzer Anzeige ausblenden.
        var dq = MainWindow?.DispatcherQueue;
        if (dq is not null)
        {
            var hide = dq.CreateTimer();
            hide.Interval = TimeSpan.FromMilliseconds(700);
            hide.Tick += (t, _) => { t.Stop(); Hud?.Hide(); };
            hide.Start();
        }
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
