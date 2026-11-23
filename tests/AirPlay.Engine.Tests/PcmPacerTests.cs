using AirPlay.Engine.Audio;

namespace AirPlay.Engine.Tests;

public class PcmPacerTests
{
    private static readonly TimeSpan OneFrame = TimeSpan.FromSeconds(352.0 / 44100);

    [Fact]
    public void Audio_goes_out_in_order_once_it_is_due()
    {
        var sent = new List<short[]>();
        var pacer = new PcmPacer(frame => sent.Add(frame.ToArray()));
        var audio = Enumerable.Range(0, 352 * 2 * 2).Select(i => (short)i).ToArray();
        pacer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.AsSpan()));

        pacer.Pump(OneFrame / 2);
        pacer.Pump(OneFrame * 1.5);

        Assert.Equal(2, sent.Count);
        Assert.Equal(audio[..704], sent[0]);
        Assert.Equal(audio[704..], sent[1]);
    }

    [Fact]
    public void A_short_wait_for_capture_sends_nothing()
    {
        var sent = 0;
        var pacer = new PcmPacer(_ => sent++);

        pacer.Pump(TimeSpan.FromMilliseconds(30));

        Assert.Equal(0, sent);
    }

    [Fact]
    public void A_longer_quiet_spell_is_filled_with_silence()
    {
        var sent = new List<short[]>();
        var pacer = new PcmPacer(frame => sent.Add(frame.ToArray()));

        pacer.Pump(TimeSpan.FromMilliseconds(500));

        Assert.NotEmpty(sent);
        Assert.All(sent, frame => Assert.All(frame, sample => Assert.Equal(0, sample)));
        Assert.Equal(sent.Count, pacer.SilentFrames);
    }

    [Fact]
    public void Capture_running_ahead_is_dropped_down_to_a_short_backlog()
    {
        var sent = 0;
        var pacer = new PcmPacer(_ => sent++);
        pacer.Write(new byte[44100 * 4 / 2]);

        pacer.Pump(OneFrame / 2);

        Assert.Equal(1, sent);
        Assert.True(pacer.DroppedFrames > 30);
    }
}
