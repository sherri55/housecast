using AirPlay.Engine.Ptp;

namespace AirPlay.Engine.Tests;

// The expected bytes were captured from OwnTone playing to a speaker that follows its clock
public class PtpMessagesTests
{
    private const ulong ClockId = 0xFFFF_0ED4_F4B7_619B;

    [Fact]
    public void Announce_matches_OwnTone() =>
        Assert.Equal(Hex("""
            1b02 004c 0000 0408 0000 0000 0000 0000 0000 0000 ffff 0ed4 f4b7 619b 8005 0000
            0000 0000 0000 0000 0000 0000 0000 0080 0621 436a 80ff ff0e d4f4 b761 9b00 0020
            0008 0008 ffff 0ed4 f4b7 619b
            """), PtpMessages.Announce(ClockId, 0));

    [Fact]
    public void Signaling_matches_OwnTone() =>
        Assert.Equal(Hex("""
            1c02 006a 0000 0408 0000 0000 0000 0000 0000 0000 ffff 0ed4 f4b7 619b 8005 0000
            0580 0000 0000 0000 0000 0000 0003 0016 000d 9300 0001 0000 0301 0000 0000 0000
            0000 0000 0000 0003 0020 000d 9300 0005 0000 0301 0000 0000 0000 0000 0000 0000
            0000 0000 0000 0000 0000
            """), PtpMessages.Signaling(ClockId, 0));

    [Fact]
    public void Sync_matches_OwnTone() =>
        Assert.Equal(Hex("""
            1002 002c 0000 0608 0000 0000 0000 0000 0000 0000 ffff 0ed4 f4b7 619b 8005 0001
            00fd 0000 0000 0000 0000 0000
            """), PtpMessages.Sync(ClockId, 1));

    [Fact]
    public void Follow_up_matches_OwnTone() =>
        Assert.Equal(Hex("""
            1802 0060 0000 0408 0000 0000 0000 0000 0000 0000 ffff 0ed4 f4b7 619b 8005 0001
            00fd 0000 0000 26d6 257c 6a92 0003 001c 0080 c200 0001 0000 0000 0000 0000 0000
            0000 0000 0000 0000 0000 0000 0003 0010 000d 9300 0004 ffff 0ed4 f4b7
            619b 0000
            """), PtpMessages.FollowUp(ClockId, 1, 9942_628_910_738));

    [Fact]
    public void Delay_response_answers_the_requester_with_its_sequence_and_arrival_time()
    {
        var request = new byte[44];
        request[0] = PtpMessages.TypeDelayRequest;
        Hex("60ab d2ff fef6 6ace 8000").CopyTo(request, 20);
        request[30] = 0x12;
        request[31] = 0x34;

        var response = PtpMessages.DelayResponse(ClockId, request, 9942_628_910_738);

        Assert.Equal(54, response.Length);
        Assert.Equal(PtpMessages.TypeDelayResponse, PtpMessages.MessageType(response));
        Assert.Equal(Hex("1234"), response[30..32]);
        Assert.Equal(Hex("0000 0000 26d6 257c 6a92"), response[34..44]);
        Assert.Equal(Hex("60ab d2ff fef6 6ace 8000"), response[44..54]);
    }

    private static byte[] Hex(string text) =>
        Convert.FromHexString(string.Concat(text.Where(Uri.IsHexDigit)));
}
