namespace VoiceAgend.App.Services;

public enum HudState
{
    Hidden,
    Recording,
    Sending,
    Processing,
    Done,
    Error,
    /// <summary>Sprachwechsel-Geste (Hotkey halten + scrollen): zeigt die gewählte Sprache.</summary>
    LanguageSwitch,
}
