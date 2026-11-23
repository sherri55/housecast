using AirPlay.Engine.Audio;

namespace AirPlay.Engine.Tests;

public class AlacFramerTests
{
    [Fact]
    public void A_frame_is_1412_bytes()
    {
        Assert.Equal(1412, AlacFramer.FrameLength);
        Assert.Equal(1412, AlacFramer.Encode(new short[352 * 2]).Length);
    }

    [Fact]
    public void The_header_says_stereo_and_uncompressed()
    {
        var frame = AlacFramer.Encode(new short[352 * 2]);

        // 001 (stereo pair) 0000 (tag) 0000 0000 0000 (unused) 0 (full frame) 00 (no shift) 1 (escape)
        Assert.Equal(0b0010_0000, frame[0]);
        Assert.Equal(0b0000_0000, frame[1]);
        Assert.Equal(0b0000_0010, frame[2]);
    }

    [Fact]
    public void Samples_come_back_out_in_order_and_the_end_tag_follows()
    {
        var samples = Enumerable.Range(0, 352 * 2).Select(i => (short)(i * 91 - 32000)).ToArray();

        var frame = AlacFramer.Encode(samples);

        var bit = 23;
        foreach (var expected in samples)
            Assert.Equal(expected, (short)Read(frame, ref bit, 16));
        Assert.Equal(7, Read(frame, ref bit, 3));
    }

    [Fact]
    public void A_wrong_sample_count_is_refused() =>
        Assert.Throws<ArgumentException>(() => AlacFramer.Encode(new short[100]));

    private static int Read(byte[] buffer, ref int bit, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++, bit++)
            value = (value << 1) | ((buffer[bit >> 3] >> (7 - (bit & 7))) & 1);
        return value;
    }
}
