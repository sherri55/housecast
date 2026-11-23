using AirPlay.Engine;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Housecast;

/// <summary>
/// Captures whatever Windows is playing and hands it to the AirPlay engine, which sends it
/// on at the speakers' pace and fills quiet spells with silence.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    // The engine takes 44.1 kHz, 16-bit, stereo PCM
    private static readonly WaveFormat EngineFormat = new(AirPlayStreamer.SampleRate, 16, 2);

    // Loopback is polled every half buffer, so a short buffer gets the audio out sooner
    private const int CaptureBufferMilliseconds = 20;

    private readonly AirPlayEngine _engine;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _captured;
    private IWaveProvider? _converted;
    private byte[] _convertBuffer = [];

    public AudioStreamer(AirPlayEngine engine) => _engine = engine;

    public void Start()
    {
        using var enumerator = new MMDeviceEnumerator();
        _capture = new LoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        _captured = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            // Return only what was captured; padding here would put tiny gaps in the sound
            ReadFully = false,
        };

        var stereo = new MultiplexingSampleProvider([_captured.ToSampleProvider()], 2);
        var resampled = new WdlResamplingSampleProvider(stereo, EngineFormat.SampleRate);
        _converted = new SampleToWaveProvider16(resampled);
        _convertBuffer = new byte[EngineFormat.AverageBytesPerSecond * CaptureBufferMilliseconds / 1000];

        _capture.DataAvailable += OnCaptured;
        _capture.StartRecording();
    }

    // Hands captured audio straight on, so nothing waits on a timer
    private void OnCaptured(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        _captured!.AddSamples(e.Buffer, 0, e.BytesRecorded);
        int count;
        while ((count = _converted!.Read(_convertBuffer, 0, _convertBuffer.Length)) > 0)
            _engine.Write(_convertBuffer.AsSpan(0, count));
    }

    public void Dispose()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
    }

    /// <summary>
    /// NAudio's WasapiLoopbackCapture with a shorter buffer than its fixed 100 ms.
    /// </summary>
    private sealed class LoopbackCapture(MMDevice device) : WasapiCapture(device, false, CaptureBufferMilliseconds)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}
