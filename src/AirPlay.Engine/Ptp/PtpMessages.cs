using System.Buffers.Binary;

namespace AirPlay.Engine.Ptp;

/// <summary>
/// The PTP (IEEE 1588 v2) messages a sender sends as AirPlay's clock master, byte for byte
/// as OwnTone's libairptp builds them (libairptp/src/ptp_msg_handle.c, MIT).
/// All fields are big-endian.
/// </summary>
public static class PtpMessages
{
    public const byte TypeSync = 0x00;
    public const byte TypeDelayRequest = 0x01;
    public const byte TypeFollowUp = 0x08;
    public const byte TypeDelayResponse = 0x09;
    public const byte TypeAnnounce = 0x0B;
    public const byte TypeSignaling = 0x0C;

    public const int HeaderLength = 34;

    // Unicast and PTP timescale, as iOS sets them; Sync and Delay_Resp also say two-step
    private const ushort FlagsUnicastTimescale = 0x0408;
    private const ushort FlagsUnicastTimescaleTwoStep = 0x0608;

    // log2 of the message interval: -3 is every 125 ms; -128 means "not periodic"
    private const sbyte LogIntervalSync = -3;
    private const sbyte LogIntervalAnnounce = 0;
    private const sbyte LogIntervalSignaling = -128;

    private const ushort TlvOrganizationExtension = 0x0003;
    private const ushort TlvPathTrace = 0x0008;
    private static readonly byte[] IeeeOrganization = [0x00, 0x80, 0xC2];
    private static readonly byte[] AppleOrganization = [0x00, 0x0D, 0x93];

    public static byte[] Announce(ulong clockId, ushort sequenceId)
    {
        var message = Header(TypeAnnounce, 76, FlagsUnicastTimescale, clockId, sequenceId, LogIntervalAnnounce);
        var body = message.AsSpan(HeaderLength);
        // originTimestamp (10) and currentUtcOffset (2) are zero; then a reserved byte
        body[13] = 128;                                         // grandmasterPriority1
        new byte[] { 0x06, 0x21, 0x43, 0x6A }.CopyTo(body[14..]); // grandmasterClockQuality
        body[18] = 128;                                         // grandmasterPriority2
        BinaryPrimitives.WriteUInt64BigEndian(body[19..], clockId);
        // stepsRemoved (2) is zero
        body[29] = 0x20;                                        // timeSource
        WriteTlv(body[30..], TlvPathTrace, BigEndian(clockId));
        return message;
    }

    /// <summary>Two-step Sync: the time follows in a Follow_Up with the same sequence id.</summary>
    public static byte[] Sync(ulong clockId, ushort sequenceId) =>
        Header(TypeSync, 44, FlagsUnicastTimescaleTwoStep, clockId, sequenceId, LogIntervalSync);

    public static byte[] FollowUp(ulong clockId, ushort sequenceId, long preciseOriginNanoseconds)
    {
        var message = Header(TypeFollowUp, 96, FlagsUnicastTimescale, clockId, sequenceId, LogIntervalSync);
        var body = message.AsSpan(HeaderLength);
        WriteTimestamp(body, preciseOriginNanoseconds);

        // IEEE 802.1AS Follow_Up information TLV, all values zero
        var followUpInfo = new byte[28];
        IeeeOrganization.CopyTo(followUpInfo, 0);
        followUpInfo[5] = 0x01;
        WriteTlv(body[10..], TlvOrganizationExtension, followUpInfo);

        // Apple's clock id TLV
        var appleClock = new byte[16];
        AppleOrganization.CopyTo(appleClock, 0);
        appleClock[5] = 0x04;
        BinaryPrimitives.WriteUInt64BigEndian(appleClock.AsSpan(6), clockId);
        WriteTlv(body[42..], TlvOrganizationExtension, appleClock);
        return message;
    }

    public static byte[] Signaling(ulong clockId, ushort sequenceId)
    {
        var message = Header(TypeSignaling, 106, FlagsUnicastTimescale, clockId, sequenceId, LogIntervalSignaling, controlField: 5);
        var body = message.AsSpan(HeaderLength);
        // targetPortIdentity (10) is zero; then two Apple TLVs whose meaning isn't known
        var first = new byte[22];
        AppleOrganization.CopyTo(first, 0);
        new byte[] { 0x00, 0x00, 0x01, 0x00, 0x00, 0x03, 0x01 }.CopyTo(first, 3);
        WriteTlv(body[10..], TlvOrganizationExtension, first);

        var second = new byte[32];
        AppleOrganization.CopyTo(second, 0);
        new byte[] { 0x00, 0x00, 0x05, 0x00, 0x00, 0x03, 0x01 }.CopyTo(second, 3);
        WriteTlv(body[36..], TlvOrganizationExtension, second);
        return message;
    }

    /// <summary>
    /// The answer to a receiver's Delay_Req: when it arrived, addressed back to the requester.
    /// </summary>
    public static byte[] DelayResponse(ulong clockId, ReadOnlySpan<byte> request, long receivedNanoseconds)
    {
        var sequenceId = BinaryPrimitives.ReadUInt16BigEndian(request[30..]);
        var message = Header(TypeDelayResponse, 54, FlagsUnicastTimescaleTwoStep, clockId, sequenceId, LogIntervalSync);
        var body = message.AsSpan(HeaderLength);
        WriteTimestamp(body, receivedNanoseconds);
        request.Slice(20, 10).CopyTo(body[10..]);  // requestingPortIdentity = the request's sourcePortIdentity
        return message;
    }

    /// <summary>The message type, from the low four bits of the first byte.</summary>
    public static int MessageType(ReadOnlySpan<byte> message) => message[0] & 0x0F;

    private static byte[] Header(byte type, int length, ushort flags, ulong clockId, ushort sequenceId, sbyte logInterval, byte controlField = 0)
    {
        var message = new byte[length];
        var header = message.AsSpan();
        header[0] = (byte)(0x10 | type);  // transportSpecific 1
        header[1] = 0x02;                 // PTP version 2
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)length);
        // domainNumber, reserved: zero
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], flags);
        // correctionField (8), reserved (4): zero
        BinaryPrimitives.WriteUInt64BigEndian(header[20..], clockId);
        BinaryPrimitives.WriteUInt16BigEndian(header[28..], 0x8005); // sourcePortIdentity port number
        BinaryPrimitives.WriteUInt16BigEndian(header[30..], sequenceId);
        header[32] = controlField;
        header[33] = (byte)logInterval;
        return message;
    }

    // seconds as 48 bits (16 high, 32 low), then nanoseconds as 32 bits
    private static void WriteTimestamp(Span<byte> destination, long nanoseconds)
    {
        var seconds = nanoseconds / 1_000_000_000;
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(seconds >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(destination[2..], (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], (uint)(nanoseconds % 1_000_000_000));
    }

    private static void WriteTlv(Span<byte> destination, ushort type, byte[] value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, type);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)value.Length);
        value.CopyTo(destination[4..]);
    }

    private static byte[] BigEndian(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
}
