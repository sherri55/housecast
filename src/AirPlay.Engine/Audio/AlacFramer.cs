namespace AirPlay.Engine.Audio;

/// <summary>
/// Wraps 16-bit stereo PCM in ALAC frames that use ALAC's "uncompressed" escape, so no
/// encoder is needed; receivers decode them like any ALAC frame. The same approach as
/// libraop's pcm_to_alac_raw. OwnTone encodes real ALAC with ffmpeg instead.
/// </summary>
public static class AlacFramer
{
    /// <summary>Samples per channel in one frame, as announced in SETUP (spf).</summary>
    public const int SamplesPerFrame = 352;

    /// <summary>
    /// Header (23 bits) + 352 × 2 channels × 16 bits + end tag (3 bits), rounded up to whole bytes.
    /// </summary>
    public const int FrameLength = (23 + SamplesPerFrame * 2 * 16 + 3 + 7) / 8;

    private const int ChannelPairElement = 1;
    private const int EndElement = 7;

    /// <param name="interleaved">Exactly <see cref="SamplesPerFrame"/> left/right sample pairs.</param>
    public static byte[] Encode(ReadOnlySpan<short> interleaved)
    {
        if (interleaved.Length != SamplesPerFrame * 2)
            throw new ArgumentException($"An ALAC frame holds {SamplesPerFrame} stereo samples.", nameof(interleaved));

        var frame = new byte[FrameLength];
        var bit = 0;

        Write(frame, ref bit, ChannelPairElement, 3);
        Write(frame, ref bit, 0, 4);   // element instance tag
        Write(frame, ref bit, 0, 12);  // unused
        Write(frame, ref bit, 0, 1);   // no sample count: the frame is the full SamplesPerFrame
        Write(frame, ref bit, 0, 2);   // no shifted bytes
        Write(frame, ref bit, 1, 1);   // escape: samples follow uncompressed

        foreach (var sample in interleaved)
            Write(frame, ref bit, (ushort)sample, 16);

        Write(frame, ref bit, EndElement, 3);
        return frame;
    }

    // Writes the low `count` bits of value, most significant first, starting at bit offset `bit`
    private static void Write(byte[] buffer, ref int bit, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--, bit++)
        {
            if (((value >> i) & 1) != 0)
                buffer[bit >> 3] |= (byte)(0x80 >> (bit & 7));
        }
    }
}
