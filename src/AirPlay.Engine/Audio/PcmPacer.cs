namespace AirPlay.Engine.Audio;

/// <summary>
/// Turns audio captured in bursts into frames handed on at exactly 44.1 kHz by our clock,
/// which is the rate <see cref="AirPlayStreamer"/> expects. When capture falls quiet (Windows
/// delivers nothing while nothing plays) the gap is filled with silence; when capture runs
/// ahead of our clock, audio is dropped so the delay stays fixed.
/// </summary>
public sealed class PcmPacer(Action<short[]> sendFrame)
{
    // 44.1 kHz, 16-bit, stereo
    public const int BytesPerSample = 4;
    private const int FrameBytes = AlacFramer.SamplesPerFrame * BytesPerSample;

    // Capture arrives about every 10 ms, so a frame may wait this long for it before
    // silence goes out instead
    private const long WaitForAudioSamples = AirPlayStreamer.SampleRate * 60 / 1000;
    // More buffered than this means capture is running fast
    private const int MostBufferedBytes = AirPlayStreamer.SampleRate * 120 / 1000 * BytesPerSample;

    private readonly byte[] _buffer = new byte[AirPlayStreamer.SampleRate * BytesPerSample];
    private readonly byte[] _frameBytes = new byte[FrameBytes];
    private readonly short[] _frame = new short[AlacFramer.SamplesPerFrame * 2];
    private int _start;
    private int _count;
    private long _samplesSent;

    public long SilentFrames { get; private set; }
    public long DroppedFrames { get; private set; }

    /// <summary>Adds captured audio: 44.1 kHz, 16-bit, stereo, little-endian.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        lock (_buffer)
        {
            foreach (var value in pcm)
            {
                // A full buffer means nothing is being sent; keep the newest audio
                if (_count == _buffer.Length)
                {
                    _start = (_start + 1) % _buffer.Length;
                    _count--;
                }
                _buffer[(_start + _count) % _buffer.Length] = value;
                _count++;
            }
        }
    }

    /// <summary>Sends every frame due by <paramref name="elapsed"/>, counted from the first call's zero.</summary>
    public void Pump(TimeSpan elapsed)
    {
        var dueSamples = elapsed.Ticks * AirPlayStreamer.SampleRate / TimeSpan.TicksPerSecond;
        while (_samplesSent < dueSamples)
        {
            lock (_buffer)
            {
                if (_count >= FrameBytes)
                {
                    Take(_frameBytes);
                    if (_count > MostBufferedBytes)
                    {
                        DroppedFrames++;
                        continue;
                    }
                }
                else if (dueSamples - _samplesSent > WaitForAudioSamples)
                {
                    Array.Clear(_frameBytes);
                    SilentFrames++;
                }
                else
                    return;
            }

            Buffer.BlockCopy(_frameBytes, 0, _frame, 0, FrameBytes);
            sendFrame(_frame);
            _samplesSent += AlacFramer.SamplesPerFrame;
        }
    }

    private void Take(byte[] destination)
    {
        for (var i = 0; i < destination.Length; i++)
            destination[i] = _buffer[(_start + i) % _buffer.Length];
        _start = (_start + destination.Length) % _buffer.Length;
        _count -= destination.Length;
    }
}
