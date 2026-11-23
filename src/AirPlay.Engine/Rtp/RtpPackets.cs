using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AirPlay.Engine.Rtp;

/// <summary>
/// The UDP packets of an AirPlay 2 realtime audio stream, as OwnTone builds them
/// (outputs/airplay.c, outputs/rtp_common.c).
/// </summary>
public static class RtpPackets
{
    public const int HeaderLength = 12;
    public const int TagLength = 16;
    public const int SyncLength = 28;

    // Payload type 96 (realtime ALAC) as announced in SETUP; the marker bit flags a new stream
    private const byte PayloadType = 0x60;
    private const byte MarkerBit = 0x80;

    /// <summary>
    /// An encrypted audio packet: clear 12-byte header, then the ALAC frame encrypted with
    /// ChaCha20-Poly1305, its 16-byte tag, and the last 8 bytes of the nonce.
    /// </summary>
    /// <param name="audioKey">The first 32 bytes of the pairing's shared secret (SETUP's shk).</param>
    /// <param name="marker">Set on the packets that start a stream.</param>
    public static byte[] Audio(ChaCha20Poly1305 audioKey, ushort sequence, uint rtpTime, ReadOnlySpan<byte> alacFrame, bool marker)
    {
        var packet = new byte[HeaderLength + alacFrame.Length + TagLength + 8];
        packet[0] = 0x80;  // RTP version 2
        packet[1] = (byte)(PayloadType | (marker ? MarkerBit : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTime);
        // SSRC (bytes 8-11) is zero in PTP sessions

        // The nonce holds the sequence number in OwnTone's (little-endian) byte order, so a
        // resent packet is identical to the original
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(nonce.AsSpan(4), sequence);

        audioKey.Encrypt(
            nonce,
            alacFrame,
            packet.AsSpan(HeaderLength, alacFrame.Length),
            packet.AsSpan(HeaderLength + alacFrame.Length, TagLength),
            packet.AsSpan(4, 8));  // authenticated: rtptime and SSRC
        nonce.AsSpan(4).CopyTo(packet.AsSpan(HeaderLength + alacFrame.Length + TagLength));
        return packet;
    }

    /// <summary>
    /// Tells the speaker that the sample at <paramref name="rtpTime"/> plays at
    /// <paramref name="clockNanoseconds"/> on the PTP clock.
    /// </summary>
    /// <param name="first">The first sync of a session carries a different first byte.</param>
    /// <param name="nextRtpTime">The rtptime of the next packet to be sent.</param>
    public static byte[] Sync(uint rtpTime, long clockNanoseconds, uint nextRtpTime, ulong clockId, bool first)
    {
        var packet = new byte[SyncLength];
        packet[0] = (byte)(first ? 0x90 : 0x80);
        packet[1] = 0xD7;
        packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTime);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), (ulong)clockNanoseconds);
        // OwnTone sends the next rtptime less the receiver's 250 ms minimum latency
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), nextRtpTime - 11025);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), clockId);
        return packet;
    }

    /// <summary>
    /// Reads a receiver's resend request (0x80 0xD5): the first sequence number and how many.
    /// </summary>
    public static (ushort First, ushort Count)? ResendRequest(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 || packet[0] != 0x80 || (packet[1] & 0x7F) != 0x55)
            return null;
        return (BinaryPrimitives.ReadUInt16BigEndian(packet[4..]), BinaryPrimitives.ReadUInt16BigEndian(packet[6..]));
    }
}
