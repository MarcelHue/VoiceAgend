using System.Text.Json;
using VoiceAgend.App.Models;

namespace VoiceAgend.App.Services;

/// <summary>
/// Synchronisiert Software-Präferenzen (Theme, Hotkey, Output-Modus, …) zwischen lokalem
/// AppSettings und dem Server-Profil pro API-Key. Gerätespezifisches (Mikrofon, Fenster-
/// geometrie, AutoStart, Server-URL) bleibt strikt lokal.
/// </summary>
public sealed class ProfileSyncService
{
    private readonly object _pushLock = new();
    private CancellationTokenSource? _pushDebounce;

    public event Action? RemoteSettingsApplied;

    /// <summary>Whitelist der Felder, die wir auf den Server pushen.</summary>
    public static IDictionary<string, object?> ExtractSyncable(AppSettings s) => new Dictionary<string, object?>
    {
        ["language"] = s.Language,
        ["enabledLanguages"] = s.EnabledLanguages,
        ["uiLanguage"] = s.UiLanguage,
        ["theme"] = s.Theme,
        ["hotkeyModifiers"] = s.HotkeyModifiers,
        ["hotkeyVirtualKey"] = s.HotkeyVirtualKey,
        ["hotkeyEnabled"] = s.HotkeyEnabled,
        ["outputMode"] = s.OutputMode.ToString(),
        ["typingSpeedCps"] = s.TypingSpeedCps,
        ["showToastOnResult"] = s.ShowToastOnResult,
        ["soundOnStart"] = s.SoundOnStart.ToString(),
        ["soundOnStop"] = s.SoundOnStop.ToString(),
        ["soundOnDone"] = s.SoundOnDone.ToString(),
        ["soundOnError"] = s.SoundOnError.ToString(),
        ["soundVolume"] = s.SoundVolume,
        ["hudEnabled"] = s.HudEnabled,
        ["hudPosition"] = s.HudPosition.ToString(),
        ["hudMargin"] = s.HudMargin,
    };

    /// <summary>Wendet einen vom Server gepullten Settings-Block auf das lokale AppSettings an.</summary>
    public static void ApplySyncable(AppSettings s, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        string? Str(string k) => payload.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string k) => payload.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : null;
        uint? UInt(string k) => payload.TryGetProperty(k, out var v) && v.TryGetUInt32(out var i) ? i : null;
        bool? Bool(string k) => payload.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
        T? EnumOf<T>(string k) where T : struct, Enum
        {
            var raw = Str(k);
            return System.Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) ? parsed : null;
        }

        if (Str("language") is { } lang) s.Language = lang;
        if (payload.TryGetProperty("enabledLanguages", out var el) && el.ValueKind == JsonValueKind.Array)
            s.EnabledLanguages = el.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList();
        if (Str("uiLanguage") is { } ui) s.UiLanguage = ui;
        if (Str("theme") is { } theme) s.Theme = theme;
        if (UInt("hotkeyModifiers") is { } hm) s.HotkeyModifiers = hm;
        if (UInt("hotkeyVirtualKey") is { } hvk) s.HotkeyVirtualKey = hvk;
        if (Bool("hotkeyEnabled") is { } he) s.HotkeyEnabled = he;
        if (EnumOf<OutputMode>("outputMode") is { } om) s.OutputMode = om;
        if (Int("typingSpeedCps") is { } ts) s.TypingSpeedCps = ts;
        if (Bool("showToastOnResult") is { } toast) s.ShowToastOnResult = toast;
        if (EnumOf<SoundChoice>("soundOnStart") is { } s1) s.SoundOnStart = s1;
        if (EnumOf<SoundChoice>("soundOnStop") is { } s2) s.SoundOnStop = s2;
        if (EnumOf<SoundChoice>("soundOnDone") is { } s3) s.SoundOnDone = s3;
        if (EnumOf<SoundChoice>("soundOnError") is { } s4) s.SoundOnError = s4;
        if (Int("soundVolume") is { } sv) s.SoundVolume = sv;
        if (Bool("hudEnabled") is { } hue) s.HudEnabled = hue;
        if (EnumOf<HudPosition>("hudPosition") is { } hp) s.HudPosition = hp;
        if (Int("hudMargin") is { } hmg) s.HudMargin = hmg;
    }

    /// <summary>
    /// Startup-Sync: Profil vom Server holen, per Timestamp entscheiden ob pull oder push.
    /// Returns true, wenn lokale Settings vom Server überschrieben wurden — die UI sollte
    /// sich dann neu laden.
    /// </summary>
    public async Task<bool> SyncOnStartupAsync(CancellationToken ct = default)
    {
        var app = App.Current;
        var s = app.Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey) || string.IsNullOrWhiteSpace(s.ServerUrl))
            return false;

        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            var p = await app.ServerApi.GetProfileAsync(baseUrl, s.ApiKey, ct);

            var serverTs = p.ClientSettingsUpdatedAt;
            var localTs = s.SettingsUpdatedAtUtc;
            var hasServer = p.ClientSettings is { ValueKind: JsonValueKind.Object };

            // Server hat keine Settings → wir pushen unsere lokalen.
            if (!hasServer)
            {
                if (localTs > DateTime.MinValue)
                {
                    Logger.Info("ProfileSync: no server settings yet → pushing local");
                    await PushNowAsync(ct);
                }
                return false;
            }

            // Wenn Server-Timestamp neuer ist → pullen
            if (serverTs is { } sts && sts > localTs.AddSeconds(1)) // 1s Toleranz gegen Drift
            {
                Logger.Info($"ProfileSync: pulling — server {sts:o} newer than local {localTs:o}");
                ApplySyncable(s, p.ClientSettings!.Value);
                s.SettingsUpdatedAtUtc = sts;
                app.SettingsStore.Save(s);
                RemoteSettingsApplied?.Invoke();
                return true;
            }

            // Lokal neuer (oder gleich) → push, damit andere Geräte profitieren
            if (localTs > (serverTs ?? DateTime.MinValue).AddSeconds(1))
            {
                Logger.Info("ProfileSync: pushing — local newer than server");
                await PushNowAsync(ct);
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn("ProfileSync startup: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Schedule einen Push nach kurzer Debounce-Zeit. Wird bei jedem lokalen Settings-Save
    /// aufgerufen, sodass schnelle Toggle-Aktionen nur einen Push erzeugen.
    /// </summary>
    public void SchedulePush(TimeSpan? debounce = null)
    {
        var d = debounce ?? TimeSpan.FromSeconds(2);
        CancellationTokenSource cts;
        lock (_pushLock)
        {
            _pushDebounce?.Cancel();
            _pushDebounce = new CancellationTokenSource();
            cts = _pushDebounce;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(d, cts.Token); }
            catch (TaskCanceledException) { return; }
            await PushNowAsync(CancellationToken.None);
        });
    }

    public async Task PushNowAsync(CancellationToken ct = default)
    {
        var app = App.Current;
        var s = app.Settings;
        if (string.IsNullOrWhiteSpace(s.ApiKey) || string.IsNullOrWhiteSpace(s.ServerUrl)) return;
        try
        {
            var baseUrl = ServerApiClient.ToHttpBase(s.ServerUrl);
            var ts = s.SettingsUpdatedAtUtc == DateTime.MinValue
                ? DateTime.UtcNow : s.SettingsUpdatedAtUtc;
            await app.ServerApi.UpdateProfileAsync(
                baseUrl, s.ApiKey,
                model: null, prompt: null, temperature: null,
                clientSettings: ExtractSyncable(s),
                clientSettingsUpdatedAtUtc: ts,
                ct: ct);
            Logger.Info($"ProfileSync: pushed (ts={ts:o})");
        }
        catch (Exception ex)
        {
            Logger.Warn("ProfileSync push: " + ex.Message);
        }
    }
}
