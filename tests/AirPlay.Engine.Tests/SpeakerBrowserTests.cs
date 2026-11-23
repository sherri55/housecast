using AirPlay.Engine.Discovery;

namespace AirPlay.Engine.Tests;

public class SpeakerBrowserTests
{
    [Theory]
    [InlineData("0x4A7FDFD5,0x3C175FDE", 0x3C175FDE_4A7FDFD5UL)]
    [InlineData("0x445F8A00,0x1C340", 0x0001C340_445F8A00UL)]
    [InlineData("0x5A7FFFF7", 0x5A7FFFF7UL)]
    [InlineData("", 0UL)]
    [InlineData("nonsense", 0UL)]
    public void ParseFeatures_combines_low_and_high_words(string text, ulong expected) =>
        Assert.Equal(expected, SpeakerBrowser.ParseFeatures(text));

    [Fact]
    public void Feature_bits_for_AirPlay_2_audio_and_PTP_are_read()
    {
        // Bedroom 2's features as announced: audio (9), PTP (41) and pairing (48) all set
        var speaker = new AirPlaySpeaker("id", "Bedroom 2", System.Net.IPAddress.Loopback, 7000, "", "", 0x0001C340_445F8A00UL);

        Assert.True(speaker.SupportsAirPlay2Audio);
        Assert.True(speaker.SupportsPtp);
    }

    [Fact]
    public void A_speaker_without_the_pairing_bit_is_not_AirPlay_2()
    {
        var speaker = new AirPlaySpeaker("id", "Old", System.Net.IPAddress.Loopback, 7000, "", "", 1UL << 9);

        Assert.False(speaker.SupportsAirPlay2Audio);
    }
}
