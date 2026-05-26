using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.IO;

namespace VoiceAgend.App.Services;

public sealed class AudioCaptureService : IDisposable
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int BitrateBps = 24_000;

    private WaveInEvent? _waveIn;
    private MemoryStream? _ogg;
    private OpusOggWriteStream? _writer;

    public bool IsRecording { get; private set; }

    public event Action? RecordingStarted;
    public event Action? RecordingStopped;

    public static IReadOnlyList<MicDevice> ListDevices()
    {
        var list = new List<MicDevice>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            list.Add(new MicDevice(i, caps.ProductName));
        }
        return list;
    }

    public void Start(int deviceNumber)
    {
        if (IsRecording) return;

        _ogg = new MemoryStream();
        var encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = BitrateBps,
            UseVBR = true,
        };
        _writer = new OpusOggWriteStream(encoder, _ogg);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 50,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
        IsRecording = true;
        RecordingStarted?.Invoke();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_writer is null) return;
        var sampleCount = e.BytesRecorded / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        _writer.WriteSamples(samples, 0, sampleCount);
    }

    /// <summary>Stops recording and returns Opus/Ogg byte stream.</summary>
    public byte[] Stop()
    {
        if (!IsRecording || _waveIn is null || _writer is null || _ogg is null)
            return Array.Empty<byte>();

        _waveIn.StopRecording();
        _waveIn.DataAvailable -= OnDataAvailable;
        Thread.Sleep(150); // letzten Buffer abwarten
        _writer.Finish();

        var bytes = _ogg.ToArray();
        _waveIn.Dispose();
        _ogg.Dispose();

        _waveIn = null;
        _writer = null;
        _ogg = null;
        IsRecording = false;
        RecordingStopped?.Invoke();
        return bytes;
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
    }

    /// <summary>
    /// Lädt eine WAV-Datei und re-encodiert sie zu Opus/Ogg mit denselben Parametern
    /// wie die Live-Aufnahme. Dadurch wird die Upload-Größe drastisch reduziert
    /// (16-bit PCM → ~24 kbps Opus = Faktor ~10–20).
    /// Mono- oder Stereo-WAV mit beliebiger Samplerate funktionieren; wir resamplen
    /// auf 16 kHz Mono und encoden.
    /// </summary>
    public static byte[] EncodeWavToOpus(byte[] wavBytes)
    {
        using var wavStream = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(wavStream);

        // Auf 16-kHz-Mono normalisieren (Whisper-Standard, und unser Live-Encoder läuft so)
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels > 1)
            source = source.ToMono();
        if (source.WaveFormat.SampleRate != SampleRate)
            source = new WdlResamplingSampleProvider(source, SampleRate);

        using var ogg = new MemoryStream();
        var encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = BitrateBps,
            UseVBR = true,
        };
        var writer = new OpusOggWriteStream(encoder, ogg);

        // Frame-für-Frame umkopieren (60 ms Frames sind für Opus VOIP ein guter Default)
        const int frameMs = 60;
        var samplesPerFrame = SampleRate * frameMs / 1000;
        var floatBuf = new float[samplesPerFrame];
        var shortBuf = new short[samplesPerFrame];
        int read;
        while ((read = source.Read(floatBuf, 0, samplesPerFrame)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var v = floatBuf[i] * 32767f;
                if (v > 32767f) v = 32767f;
                else if (v < -32768f) v = -32768f;
                shortBuf[i] = (short)v;
            }
            writer.WriteSamples(shortBuf, 0, read);
        }
        writer.Finish();
        return ogg.ToArray();
    }
}

public sealed record MicDevice(int Number, string Name);
