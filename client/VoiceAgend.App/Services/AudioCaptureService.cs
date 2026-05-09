using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;
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
}

public sealed record MicDevice(int Number, string Name);
