using System.Buffers.Binary;
using System.Security.Cryptography;
using AirPlay.Engine.Rtp;

namespace AirPlay.Engine.Tests;

public class RtpPacketsTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void An_audio_packet_is_header_ciphertext_tag_and_nonce_tail()
    {
        using var key = new ChaCha20Poly1305(Key);
        var frame = Enumerable.Range(0, 1412).Select(i => (byte)i).ToArray();

        var packet = RtpPackets.Audio(key, sequence: 0x1234, rtpTime: 0xA1B2C3D4, frame, marker: true);

        Assert.Equal(12 + 1412 + 16 + 8, packet.Length);
        Assert.Equal(new byte[] { 0x80, 0xE0, 0x12, 0x34, 0xA1, 0xB2, 0xC3, 0xD4, 0, 0, 0, 0 }, packet[..12]);
        Assert.Equal(new byte[] { 0x34, 0x12, 0, 0, 0, 0, 0, 0 }, packet[^8..]);
    }

    [Fact]
    public void The_receiver_can_decrypt_the_audio_with_the_nonce_and_header_it_is_sent()
    {
        using var key = new ChaCha20Poly1305(Key);
        var frame = Enumerable.Range(0, 1412).Select(i => (byte)(i * 7)).ToArray();

        var packet = RtpPackets.Audio(key, 7, 1000, frame, marker: false);

        var nonce = new byte[12];
        packet[^8..].CopyTo(nonce, 4);
        var decrypted = new byte[1412];
        key.Decrypt(nonce, packet.AsSpan(12, 1412), packet.AsSpan(12 + 1412, 16), decrypted, packet.AsSpan(4, 8));
        Assert.Equal(frame, decrypted);
        Assert.Equal(0x60, packet[1]);
    }

    [Fact]
    public void A_sync_packet_carries_the_timing_and_the_clock()
    {
        var first = RtpPackets.Sync(rtpTime: 100_000, clockNanoseconds: 0x0102030405060708, nextRtpTime: 200_000, clockId: 0xFFFF0ED4F4B7619B, first: true);
        var later = RtpPackets.Sync(100_000, 0, 200_000, 0, first: false);

        Assert.Equal(28, first.Length);
        Assert.Equal(new byte[] { 0x90, 0xD7, 0x00, 0x06 }, first[..4]);
        Assert.Equal(0x80, later[0]);
        Assert.Equal(100_000u, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(4)));
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64BigEndian(first.AsSpan(8)));
        Assert.Equal(200_000u - 11025, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(16)));
        Assert.Equal(0xFFFF0ED4F4B7619BUL, BinaryPrimitives.ReadUInt64BigEndian(first.AsSpan(20)));
    }

    [Fact]
    public void Reads_a_resend_request_and_ignores_other_packets()
    {
        Assert.Equal(((ushort)0x1234, (ushort)3), RtpPackets.ResendRequest([0x80, 0xD5, 0x00, 0x01, 0x12, 0x34, 0x00, 0x03]));
        Assert.Null(RtpPackets.ResendRequest([0x80, 0xD7, 0x00, 0x01, 0x12, 0x34, 0x00, 0x03]));
        Assert.Null(RtpPackets.ResendRequest([0x80, 0xD5]));
    }
}
