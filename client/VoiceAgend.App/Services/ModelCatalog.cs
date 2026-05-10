namespace VoiceAgend.App.Services;

/// <summary>Kuratierte Liste empfohlener Whisper-Modelle für den Install-Button.</summary>
public static class ModelCatalog
{
    public sealed record Entry(string Id, string Label, string SizeApprox, string Hint);

    public static IReadOnlyList<Entry> All { get; } = new[]
    {
        new Entry("Systran/faster-whisper-tiny",
            "Tiny (multilingual)", "~75 MB",
            "Schnell, geringer Speicherbedarf — Qualität für Diktat ausreichend bei klarer Sprache."),

        new Entry("Systran/faster-whisper-base",
            "Base (multilingual)", "~150 MB",
            "Gute Balance zwischen Speed und Qualität für mobile/schwache Server."),

        new Entry("Systran/faster-whisper-small",
            "Small (multilingual)", "~500 MB",
            "Spürbar besser als Base, immer noch sehr schnell auf CPU."),

        new Entry("Systran/faster-whisper-medium",
            "Medium (multilingual)", "~1.5 GB",
            "Default-Empfehlung. Auf modernen 6-Kernern ungefähr Echtzeit. Guter Trade-off."),

        new Entry("Systran/faster-whisper-large-v3",
            "Large v3 (multilingual)", "~3 GB",
            "Höchste Qualität, deutlich langsamer auf CPU (~3× Echtzeit)."),

        new Entry("Systran/faster-distil-whisper-large-v3",
            "Distil-Large v3 (English-fokussiert)", "~1.5 GB",
            "Destillierte Variante — fast Large-Qualität bei Medium-Speed. English deutlich besser als andere Sprachen."),
    };
}
