using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

const int SampleRate = 16000;
const int Channels = 1;
const int BitrateBps = 24_000;

var serverUrl = Environment.GetEnvironmentVariable("VOICEAGEND_URL");
if (string.IsNullOrWhiteSpace(serverUrl))
{
    Console.Error.WriteLine("ERROR: VOICEAGEND_URL environment variable is required.");
    Console.Error.WriteLine("  $env:VOICEAGEND_URL = \"wss://your-server.example.com/ws/transcribe\"");
    return 1;
}
var apiKey = Environment.GetEnvironmentVariable("VOICEAGEND_API_KEY");
var language = Environment.GetEnvironmentVariable("VOICEAGEND_LANGUAGE") ?? "de";

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("ERROR: VOICEAGEND_API_KEY environment variable is required.");
    Console.Error.WriteLine("Set it via PowerShell:");
    Console.Error.WriteLine("  $env:VOICEAGEND_API_KEY = \"va_xxxxxxx\"");
    return 1;
}

Console.WriteLine($"Server   : {serverUrl}");
Console.WriteLine($"Language : {language}");
Console.WriteLine($"Audio    : {SampleRate} Hz mono Opus @ {BitrateBps / 1000} kbps");
Console.WriteLine();

// 1) Aufnahme vom Standard-Mikrofon, Encoding direkt nach Opus/Ogg ins Memory
using var oggStream = new MemoryStream();

var encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP)
{
    Bitrate = BitrateBps,
    UseVBR = true,
};

var oggWriter = new OpusOggWriteStream(encoder, oggStream);

using var waveIn = new WaveInEvent
{
    WaveFormat = new WaveFormat(SampleRate, 16, Channels),
    BufferMilliseconds = 50,
};

waveIn.DataAvailable += (_, e) =>
{
    var sampleCount = e.BytesRecorded / 2;
    var samples = new short[sampleCount];
    Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
    oggWriter.WriteSamples(samples, 0, sampleCount);
};

Console.Write("[REC] Aufnahme läuft — ENTER drücken zum Beenden... ");
waveIn.StartRecording();
var t0 = DateTime.UtcNow;
Console.ReadLine();
waveIn.StopRecording();
Thread.Sleep(150); // letzte DataAvailable-Events abwarten
oggWriter.Finish();
var recordedSec = (DateTime.UtcNow - t0).TotalSeconds;

var audio = oggStream.ToArray();
Console.WriteLine($"\n[REC] {recordedSec:F1}s aufgenommen, {audio.Length / 1024.0:F1} KB Opus/Ogg");

// 2) WebSocket-Verbindung + Header
using var ws = new ClientWebSocket();
ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
Console.WriteLine($"[WS]  Verbinde mit {serverUrl}...");
await ws.ConnectAsync(new Uri(serverUrl), CancellationToken.None);
Console.WriteLine("[WS]  Verbunden.");

var header = JsonSerializer.SerializeToUtf8Bytes(new { api_key = apiKey, language });
await ws.SendAsync(header, WebSocketMessageType.Text, true, CancellationToken.None);

// 3) Audio in Chunks senden (jeder Chunk = eigene WS-Message)
const int chunkSize = 32 * 1024;
for (var offset = 0; offset < audio.Length; offset += chunkSize)
{
    var len = Math.Min(chunkSize, audio.Length - offset);
    await ws.SendAsync(new ArraySegment<byte>(audio, offset, len),
        WebSocketMessageType.Binary, true, CancellationToken.None);
}

var endMsg = JsonSerializer.SerializeToUtf8Bytes(new { end = true });
await ws.SendAsync(endMsg, WebSocketMessageType.Text, true, CancellationToken.None);
Console.WriteLine("[WS]  Audio gesendet, warte auf Antwort...");

// 4) Antworten empfangen bis Server schließt oder finale Antwort kommt
var recvBuf = new byte[64 * 1024];
while (ws.State == WebSocketState.Open)
{
    using var msg = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await ws.ReceiveAsync(recvBuf, CancellationToken.None);
        msg.Write(recvBuf, 0, result.Count);
    } while (!result.EndOfMessage);

    if (result.MessageType == WebSocketMessageType.Close) break;

    var json = Encoding.UTF8.GetString(msg.ToArray());
    Console.WriteLine($"[WS]  {json}");

    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("text", out var textEl))
    {
        Console.WriteLine();
        Console.WriteLine("─── Transkript ──────────────────────────────");
        Console.WriteLine(textEl.GetString());
        Console.WriteLine("─────────────────────────────────────────────");
        break;
    }
    if (doc.RootElement.TryGetProperty("error", out var err))
    {
        Console.Error.WriteLine($"Server-Fehler: {err.GetString()}");
        return 2;
    }
}

if (ws.State == WebSocketState.Open)
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

return 0;
