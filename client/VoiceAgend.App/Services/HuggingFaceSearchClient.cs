using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VoiceAgend.App.Services;

/// <summary>
/// Sucht faster-whisper-kompatible Modelle direkt auf der Hugging-Face-Hub-API.
/// Kein Auth nötig — die Public Models endpoint ist anonym lesbar.
/// </summary>
public sealed class HuggingFaceSearchClient
{
    public sealed record SearchResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("modelId")] string? ModelId,
        [property: JsonPropertyName("downloads")] int Downloads,
        [property: JsonPropertyName("likes")] int Likes,
        [property: JsonPropertyName("lastModified")] string? LastModified,
        [property: JsonPropertyName("tags")] string[]? Tags);

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://huggingface.co"),
        Timeout = TimeSpan.FromSeconds(10),
    };

    static HuggingFaceSearchClient()
    {
        // HF schickt 403 ohne UA — irgendwas Plausibles reicht.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceAgend/1.0 (+https://github.com/)");
    }

    /// <summary>
    /// Sucht Whisper-Modelle, die als CTranslate2 (= faster-whisper-kompatibel) vorliegen.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int limit = 30, CancellationToken ct = default)
    {
        // HF-Filter:
        //   filter=ctranslate2  → nur CTranslate2-konvertierte Repos
        //   search=...          → Volltext auf Repo-Name/Beschreibung
        //   sort=downloads      → meistgenutzte zuerst (gute Heuristik für Qualität/Stabilität)
        // Wir kombinieren Such-Term mit "whisper", damit auch leere Eingaben sinnvoll listen.
        var searchTerm = string.IsNullOrWhiteSpace(query) ? "whisper" : $"whisper {query.Trim()}";
        var url = $"/api/models?search={Uri.EscapeDataString(searchTerm)}"
                + $"&filter=ctranslate2&sort=downloads&direction=-1&limit={limit}";

        try
        {
            var results = await Http.GetFromJsonAsync<List<SearchResult>>(url, ct);
            return results ?? new List<SearchResult>();
        }
        catch (Exception ex)
        {
            Logger.Warn("HF search failed: " + ex.Message);
            return Array.Empty<SearchResult>();
        }
    }
}
