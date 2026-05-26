using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAgend.App.Services;

public sealed class ServerApiClient
{
    public sealed record Model(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("owned_by")] string? OwnedBy);

    public sealed record Profile(
        [property: JsonPropertyName("api_key_id")] int ApiKeyId,
        [property: JsonPropertyName("api_key_name")] string ApiKeyName,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("server_default_model")] string? ServerDefaultModel = null);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<List<Model>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        using var http = Build(baseUrl, apiKey);
        var r = await http.GetAsync("/api/v1/models", ct);
        r.EnsureSuccessStatusCode();
        var list = await r.Content.ReadFromJsonAsync<List<Model>>(JsonOpts, ct);
        return list ?? new List<Model>();
    }

    public async Task<Profile> GetProfileAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        using var http = Build(baseUrl, apiKey);
        var r = await http.GetAsync("/api/v1/profile", ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Profile>(JsonOpts, ct))!;
    }

    public async Task InstallModelAsync(
        string baseUrl, string apiKey, string modelId,
        CancellationToken ct = default)
    {
        // Server-Side timeout ist auf 10 min — Client gibt etwas mehr (12 min).
        using var http = Build(baseUrl, apiKey);
        http.Timeout = TimeSpan.FromMinutes(12);
        var r = await http.PostAsync($"/api/v1/models/{modelId}", content: null, ct);
        if (!r.IsSuccessStatusCode)
        {
            var body = await r.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {body}");
        }
    }

    public async Task UninstallModelAsync(
        string baseUrl, string apiKey, string modelId,
        CancellationToken ct = default)
    {
        using var http = Build(baseUrl, apiKey);
        http.Timeout = TimeSpan.FromMinutes(1);
        var r = await http.DeleteAsync($"/api/v1/models/{modelId}", ct);
        if (!r.IsSuccessStatusCode)
        {
            var body = await r.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {body}");
        }
    }

    public async Task<Profile> UpdateProfileAsync(
        string baseUrl, string apiKey,
        string? model, string? prompt, double? temperature,
        CancellationToken ct = default)
    {
        using var http = Build(baseUrl, apiKey);
        var payload = new Dictionary<string, object?>();
        if (model is not null) payload["model"] = model;
        if (prompt is not null) payload["prompt"] = prompt;
        if (temperature is not null) payload["temperature"] = temperature;
        var r = await http.PutAsJsonAsync("/api/v1/profile", payload, JsonOpts, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Profile>(JsonOpts, ct))!;
    }

    /// <summary>Konvertiert wss:// → https:// und entfernt /ws/transcribe-Pfad.</summary>
    public static string ToHttpBase(string wsUrl)
    {
        var uri = new Uri(wsUrl);
        var scheme = uri.Scheme switch
        {
            "wss" => "https",
            "ws" => "http",
            _ => uri.Scheme,
        };
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        return $"{scheme}://{uri.Host}{port}";
    }

    private static HttpClient Build(string baseUrl, string apiKey)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}
