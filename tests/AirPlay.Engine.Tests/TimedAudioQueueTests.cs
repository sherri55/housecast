using AirPlay.Engine.Audio;

namespace AirPlay.Engine.Tests;

public class TimedAudioQueueTests
{
    private const long Second = 1_000_000_000;
    private static readonly long FrameTime = AlacFramer.SamplesPerFrame * Second / 44100;

    [Fact]
    public void Frames_follow_on_at_352_samples_each_from_the_first_audio()
    {
        var queue = new TimedAudioQueue();
        queue.Add(Stereo(1, 352 * 2), 10 * Second);

        var due = queue.TakeDue(10 * Second + FrameTime);

        Assert.Equal([10 * Second, 10 * Second + FrameTime], due.Select(d => d.PlayAt));
        Assert.All(due, d => Assert.All(d.Frame, sample => Assert.Equal(1, sample)));
    }

    [Fact]
    public void A_pause_is_sent_as_silence_and_the_audio_after_it_lands_at_its_time()
    {
        var queue = new TimedAudioQueue();
        queue.Add(Stereo(1, 352), 10 * Second);
        // Resumed 2 s later
        queue.Add(Stereo(2, 352), 12 * Second);

        var due = queue.TakeDue(13 * Second);

        Assert.All(due[0].Frame, sample => Assert.Equal(1, sample));
        Assert.All(due[1..250].SelectMany(d => d.Frame), sample => Assert.Equal(0, sample));
        // 2 s is sample 88200: frame 250, 200 samples in
        Assert.Equal(0, due[250].Frame[2 * 199]);
        Assert.Equal(2, due[250].Frame[2 * 200]);
        Assert.True(due.Zip(due.Skip(1)).All(pair => pair.Second.PlayAt - pair.First.PlayAt is >= 7_981_000 and <= 7_982_000));
    }

    [Fact]
    public void Audio_for_a_moment_already_sent_is_dropped()
    {
        var queue = new TimedAudioQueue();
        queue.Add(Stereo(1, 352 * 3), 10 * Second);
        queue.TakeDue(10 * Second);

        // A seek's audio overlapping the frame already out replaces only what follows it
        queue.Add(Stereo(7, 352 * 2), 10 * Second);
        var due = queue.TakeDue(10 * Second + 2 * FrameTime);

        Assert.All(due[0].Frame, sample => Assert.Equal(7, sample));
        Assert.All(due[1].Frame, sample => Assert.Equal(1, sample));
    }

    [Fact]
    public void After_clear_the_next_audio_starts_a_new_run()
    {
        var queue = new TimedAudioQueue();
        queue.Add(Stereo(1, 352), 10 * Second);
        queue.TakeDue(10 * Second);
        queue.Clear();

        queue.Add(Stereo(3, 352), 500 * Second);

        Assert.Empty(queue.TakeDue(499 * Second));
        Assert.Equal(500 * Second, queue.TakeDue(500 * Second).Single().PlayAt);
    }

    private static short[] Stereo(short value, int samples) => Enumerable.Repeat(value, samples * 2).ToArray();
}
