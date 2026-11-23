using System.Buffers.Binary;
using System.Security.Cryptography;
using AirPlay.Engine.Ptp;
using AirPlay.Engine.Receiver;

namespace AirPlay.Engine.Tests;

public class BufferedAudioTests
{
    [Fact]
    public async Task A_packet_from_the_sender_reads_and_decrypts()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var stream = new MemoryStream([.. Framed(key, 0xCD8E34, 987654321, [1, 2, 3, 4, 5]), .. Framed(key, 7, 8, [9])]);
        using var audioKey = new ChaCha20Poly1305(key);

        var first = BufferedAudioPacket.Open(audioKey, (await BufferedAudioPacket.ReadAsync(stream, default))!);
        var second = BufferedAudioPacket.Open(audioKey, (await BufferedAudioPacket.ReadAsync(stream, default))!);

        Assert.Equal((0xCD8E34u, 987654321u), (first.Sequence, first.RtpTime));
        Assert.Equal([1, 2, 3, 4, 5], first.Aac);
        Assert.Equal([9], second.Aac);
        Assert.Null(await BufferedAudioPacket.ReadAsync(stream, default));
    }

    [Fact]
    public void A_packet_sealed_with_another_key_is_refused()
    {
        var packet = Framed(RandomNumberGenerator.GetBytes(32), 1, 2, [3])[2..];
        using var otherKey = new ChaCha20Poly1305(RandomNumberGenerator.GetBytes(32));

        Assert.ThrowsAny<CryptographicException>(() => BufferedAudioPacket.Open(otherKey, packet));
    }

    [Fact]
    public void The_follower_keeps_the_offset_the_network_delayed_least()
    {
        const ulong iPhone = 0x6058_1234_5678_9ABC;
        var follower = new PtpFollower();

        // Sent at 100 s on the iPhone's clock; arrives at 40 s on ours after 3 ms
        follower.Handle(PtpMessages.Sync(iPhone, 1), 40_003_000_000);
        follower.Handle(PtpMessages.FollowUp(iPhone, 1, 100_000_000_000), 40_004_000_000);
        // A second later, delayed only 1 ms
        follower.Handle(PtpMessages.Sync(iPhone, 2), 41_001_000_000);
        follower.Handle(PtpMessages.FollowUp(iPhone, 2, 101_000_000_000), 41_002_000_000);

        Assert.True(follower.TryGetOffset(iPhone, out var offset));
        Assert.Equal(60_000_000_000 - 1_000_000, offset);
        Assert.False(follower.TryGetOffset(0x1234, out _));
    }

    [Fact]
    public async Task Playout_follows_the_anchor_and_skips_what_was_flushed()
    {
        const ulong iPhone = 0x6058_1234_5678_9ABC;
        var follower = new PtpFollower();
        // The iPhone's clock reads ours plus 10 s
        var now = EngineClock.NowNanoseconds;
        follower.Handle(PtpMessages.Sync(iPhone, 1), now);
        follower.Handle(PtpMessages.FollowUp(iPhone, 1, now + 10_000_000_000), now);

        var played = new List<ReceivedAudio>();
        await using (var player = new BufferedAudioPlayer(follower, TimeSpan.Zero, audio => { lock (played) played.Add(audio); }, _ => { }))
        {
            var frames = AacDecoderTests.Encode(AacDecoderTests.Tone(440, seconds: 1));
            for (var i = 0; i < 20; i++)
                player.Add(new BufferedAudioPacket((uint)(100 + i), (uint)(5000 + 1024 * i), frames[i]));
            player.Flush(null, 110);
            // RTP time 5000 plays 100 ms from now
            player.SetAnchor(5000, now + 10_100_000_000, iPhone);
            player.SetRate(true);
            await Task.Delay(800);
        }

        Assert.NotEmpty(played);
        Assert.All(played, audio => Assert.True(audio.RtpTime >= 5000 + 1024 * 10));
        var tenth = played.First();
        Assert.Equal(now + 100_000_000 + 10L * 1024 * 1_000_000_000 / 44100, tenth.PlayAtNanoseconds);
        Assert.True(played.Zip(played.Skip(1)).All(pair => pair.Second.RtpTime > pair.First.RtpTime));
    }

    private static byte[] Framed(byte[] key, uint sequence, uint rtpTime, byte[] aac)
    {
        var packet = new byte[12 + aac.Length + 16 + 8];
        BinaryPrimitives.WriteUInt32BigEndian(packet, 0x8000_0000 | sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTime);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce.AsSpan(4));
        using var cipher = new ChaCha20Poly1305(key);
        cipher.Encrypt(nonce, aac, packet.AsSpan(12, aac.Length), packet.AsSpan(12 + aac.Length, 16), packet.AsSpan(4, 8));
        nonce.AsSpan(4).CopyTo(packet.AsSpan(packet.Length - 8));

        var framed = new byte[packet.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)framed.Length);
        packet.CopyTo(framed, 2);
        return framed;
    }
}
