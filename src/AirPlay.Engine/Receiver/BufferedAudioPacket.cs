using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AirPlay.Engine.Receiver;

/// <summary>
/// One packet of a buffered (type 103) stream, which a sender sends over TCP well ahead of
/// time: a 2-byte big-endian length that counts itself, a 12-byte RTP header, the AAC frame
/// encrypted with ChaCha20-Poly1305 and its 16-byte tag, then the last 8 bytes of the nonce.
/// The layout is shairport-sync's (player.c, MIT).
/// </summary>
internal sealed record BufferedAudioPacket(uint Sequence, uint RtpTime, byte[] Aac)
{
    private const int HeaderLength = 12;
    private const int TagLength = 16;
    private const int NonceTailLength = 8;

    /// <summary>The next packet's bytes after its length; null when the sender closed the connection.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[2];
        try
        {
            await stream.ReadExactlyAsync(length, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        var packet = new byte[BinaryPrimitives.ReadUInt16BigEndian(length) - 2];
        await stream.ReadExactlyAsync(packet, cancellationToken);
        return packet;
    }

    /// <param name="audioKey">The stream's key: SETUP's shk.</param>
    /// <exception cref="CryptographicException">The packet isn't one this key sealed.</exception>
    public static BufferedAudioPacket Open(ChaCha20Poly1305 audioKey, ReadOnlySpan<byte> packet)
    {
        var aacLength = packet.Length - HeaderLength - TagLength - NonceTailLength;
        if (aacLength < 0)
            throw new CryptographicException("Audio packet too short.");

        var nonce = new byte[12];
        packet[^NonceTailLength..].CopyTo(nonce.AsSpan(4));
        var aac = new byte[aacLength];
        audioKey.Decrypt(
            nonce,
            packet.Slice(HeaderLength, aacLength),
            packet.Slice(HeaderLength + aacLength, TagLength),
            aac,
            packet.Slice(4, 8));  // authenticated: rtptime and SSRC

        // The sequence number is the header's bytes 1 to 3
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(packet) & 0xFF_FFFF;
        return new BufferedAudioPacket(sequence, BinaryPrimitives.ReadUInt32BigEndian(packet[4..]), aac);
    }
}
