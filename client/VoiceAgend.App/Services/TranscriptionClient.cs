using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceAgend.App.Services;

public sealed class TranscriptionClient
{
    public sealed record Result(string Text, string? Language, int ProcessingMs);

    public async Task<Result> TranscribeAsync(
        string serverUrl, string apiKey, string? language, byte[] audioOggOpus,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        ws.Options.HttpVersion = HttpVersion.Version11;
        ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        progress?.Report("Verbinde…");
        await ws.ConnectAsync(new Uri(serverUrl), ct);

        var header = JsonSerializer.SerializeToUtf8Bytes(new { api_key = apiKey, language });
        await ws.SendAsync(header, WebSocketMessageType.Text, true, ct);

        progress?.Report("Sende Audio…");
        const int chunk = 32 * 1024;
        for (var off = 0; off < audioOggOpus.Length; off += chunk)
        {
            var len = Math.Min(chunk, audioOggOpus.Length - off);
            await ws.SendAsync(new ArraySegment<byte>(audioOggOpus, off, len),
                WebSocketMessageType.Binary, true, ct);
        }
        var endMsg = JsonSerializer.SerializeToUtf8Bytes(new { end = true });
        await ws.SendAsync(endMsg, WebSocketMessageType.Text, true, ct);

        progress?.Report("Warte auf Transkription…");
        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var msg = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct);
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType == WebSocketMessageType.Close) break;

            var json = Encoding.UTF8.GetString(msg.ToArray());
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
                throw new InvalidOperationException($"Server-Fehler: {err.GetString()} {detail}");
            }
            if (doc.RootElement.TryGetProperty("text", out var text))
            {
                var lang = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() : null;
                var ms = doc.RootElement.TryGetProperty("processing_ms", out var p) ? p.GetInt32() : 0;
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
                return new Result(text.GetString() ?? "", lang, ms);
            }
        }
        throw new InvalidOperationException("Verbindung geschlossen ohne Ergebnis.");
    }
}
