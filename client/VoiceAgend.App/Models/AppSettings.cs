namespace VoiceAgend.App.Models;

public enum OutputMode
{
    Clipboard,
    Type,
    Notification,
}

public enum HudPosition
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, MiddleCenter, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
}

public enum SoundChoice
{
    None,
    Chimes,
    Chord,
    Ding,
    Notify,
    Tada,
    Beep,
}

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Language { get; set; } = "";

    /// <summary>UI-Sprache (eigenständig von der Spracherkennungs-Sprache).</summary>
    public string UiLanguage { get; set; } = "de";

    /// <summary>WaveIn-Device-Number; -1 = Windows-Default.</summary>
    public int MicDeviceNumber { get; set; } = -1;
    public string MicDeviceName { get; set; } = "Standard";

    /// <summary>Hotkey-Modifier (Win32-Konstanten: Alt=1, Ctrl=2, Shift=4, Win=8).</summary>
    public uint HotkeyModifiers { get; set; } = 2 | 4; // Ctrl+Shift
    /// <summary>Hotkey-Virtual-Key-Code (z. B. 'R' = 0x52).</summary>
    public uint HotkeyVirtualKey { get; set; } = 0x52; // R
    /// <summary>Master-Schalter: ist der globale Hotkey aktiv?</summary>
    public bool HotkeyEnabled { get; set; } = true;

    public OutputMode OutputMode { get; set; } = OutputMode.Clipboard;
    public bool ShowToastOnResult { get; set; } = true;

    // Sound pro Event + globale Lautstärke
    public SoundChoice SoundOnStart { get; set; } = SoundChoice.Ding;
    public SoundChoice SoundOnStop { get; set; } = SoundChoice.Chord;
    public SoundChoice SoundOnDone { get; set; } = SoundChoice.Chimes;
    public SoundChoice SoundOnError { get; set; } = SoundChoice.Beep;
    public int SoundVolume { get; set; } = 70; // 0–100

    // Legacy (werden ignoriert, nur für Settings-Migration ohne Crash)
    public bool? PlaySoundOnStart { get; set; }
    public bool? PlaySoundOnStop { get; set; }

    public bool HudEnabled { get; set; } = true;
    public HudPosition HudPosition { get; set; } = HudPosition.TopRight;
    public int HudMargin { get; set; } = 24;

    public string HotkeyDisplay()
    {
        var parts = new List<string>();
        if ((HotkeyModifiers & 8) != 0) parts.Add("Win");
        if ((HotkeyModifiers & 2) != 0) parts.Add("Ctrl");
        if ((HotkeyModifiers & 1) != 0) parts.Add("Alt");
        if ((HotkeyModifiers & 4) != 0) parts.Add("Shift");
        parts.Add(VkToString(HotkeyVirtualKey));
        return string.Join("+", parts);
    }

    public static string VkToString(uint vk)
    {
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        return $"VK_{vk:X}";
    }
}
