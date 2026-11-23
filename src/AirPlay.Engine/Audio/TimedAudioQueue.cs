namespace AirPlay.Engine.Audio;

/// <summary>
/// Audio that must play at given moments, cut into one unbroken run of ALAC-sized frames:
/// a frame for every 352 samples of time, silent where nothing was queued. Speakers then
/// hear pauses and seeks as silence or a jump in the sound, never a jump in its timing,
/// which they don't follow.
/// </summary>
public sealed class TimedAudioQueue
{
    private const int SampleRate = 44100;
    private const int FrameLength = AlacFramer.SamplesPerFrame * 2;
    // Audio queued further ahead than this is a mistake in its time; 190 s of stereo samples
    private const int MaxQueuedLength = 1 << 24;

    // The run's sample 0 plays at _origin; _taken samples have gone out, and _queued holds
    // the interleaved samples that follow them
    private readonly List<short> _queued = [];
    private long _origin = -1;
    private long _taken;

    /// <summary>Queues stereo samples of any length to play from <paramref name="playAtNanoseconds"/>; samples for a moment already taken are dropped.</summary>
    public void Add(ReadOnlySpan<short> interleaved, long playAtNanoseconds)
    {
        lock (_queued)
        {
            if (_origin < 0)
                _origin = playAtNanoseconds;
            var position = (long)Math.Round((playAtNanoseconds - _origin) * (double)SampleRate / 1_000_000_000) - _taken;
            var skip = (int)Math.Clamp(-position * 2, 0, interleaved.Length);
            var start = (int)Math.Clamp(position * 2, 0, MaxQueuedLength);
            if (start == MaxQueuedLength)
                return;
            while (_queued.Count < start + interleaved.Length - skip)
                _queued.Add(0);
            for (var i = skip; i < interleaved.Length; i++)
                _queued[start + i - skip] = interleaved[i];
        }
    }

    /// <summary>Every frame that plays by <paramref name="untilNanoseconds"/>, with when it plays.</summary>
    public List<(short[] Frame, long PlayAt)> TakeDue(long untilNanoseconds)
    {
        var due = new List<(short[], long)>();
        lock (_queued)
        {
            if (_origin < 0)
                return due;
            long playAt;
            while ((playAt = _origin + _taken * 1_000_000_000 / SampleRate) <= untilNanoseconds)
            {
                var frame = new short[FrameLength];
                var available = Math.Min(FrameLength, _queued.Count);
                _queued.CopyTo(0, frame, 0, available);
                _queued.RemoveRange(0, available);
                due.Add((frame, playAt));
                _taken += AlacFramer.SamplesPerFrame;
            }
        }
        return due;
    }

    /// <summary>Drops what is queued and ends the run; the next <see cref="Add"/> starts a new one.</summary>
    public void Clear()
    {
        lock (_queued)
        {
            _queued.Clear();
            _origin = -1;
            _taken = 0;
        }
    }
}
