namespace VoiceAgend.App.Services;

/// <summary>Kuratierte Liste empfohlener Whisper-Modelle für den Install-Button.</summary>
public static class ModelCatalog
{
    public sealed record Entry(
        string Id,
        string ShortName,
        string Tag,
        string Label,
        string SizeApprox,
        double Rtf,
        string Hint);

    /// <summary>Hint-Keys werden zur Anzeige durch LocalizationService aufgelöst.</summary>
    public static IReadOnlyList<Entry> All { get; } = new[]
    {
        new Entry("Systran/faster-whisper-tiny",
            "tiny", "multilingual",
            "Tiny", "~75 MB", 0.15, "Models.Hint.Tiny"),

        new Entry("Systran/faster-whisper-base",
            "base", "multilingual",
            "Base", "~150 MB", 0.25, "Models.Hint.Base"),

        new Entry("Systran/faster-whisper-small",
            "small", "multilingual",
            "Small", "~500 MB", 0.55, "Models.Hint.Small"),

        new Entry("Systran/faster-whisper-medium",
            "medium", "multilingual",
            "Medium", "~1.5 GB", 0.91, "Models.Hint.Medium"),

        new Entry("Systran/faster-whisper-large-v3",
            "large-v3", "multilingual",
            "Large v3", "~3 GB", 3.10, "Models.Hint.LargeV3"),

        new Entry("Systran/faster-distil-whisper-large-v3",
            "distil-large-v3", "english",
            "Distil-Large v3", "~1.5 GB", 1.10, "Models.Hint.DistilLargeV3"),
    };
}
