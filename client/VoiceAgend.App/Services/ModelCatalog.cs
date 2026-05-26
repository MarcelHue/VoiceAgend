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

    /// <summary>
    /// Leer — vordefinierte Liste wurde entfernt. Stattdessen werden die tatsächlich
    /// installierten Modelle des Servers oben angezeigt; weitere Modelle findet man
    /// über die HuggingFace-Suche darunter.
    /// </summary>
    public static IReadOnlyList<Entry> All { get; } = Array.Empty<Entry>();
}
