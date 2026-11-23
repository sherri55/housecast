namespace AirPlay.Engine.Pairing;

/// <summary>
/// The HomeKit TLV8 format pairing messages use: a type byte, a length byte and up to
/// 255 bytes of value, with longer values split into consecutive items of the same type.
/// Ported from OwnTone's pair_ap/pair-tlv.c (MIT).
/// </summary>
public static class Tlv8
{
    public const byte Method = 0;
    public const byte Identifier = 1;
    public const byte Salt = 2;
    public const byte PublicKey = 3;
    public const byte Proof = 4;
    public const byte EncryptedData = 5;
    public const byte State = 6;
    public const byte Error = 7;
    public const byte Signature = 10;
    public const byte Flags = 19;

    public static byte[] Encode(params (byte Type, byte[] Value)[] items)
    {
        var output = new List<byte>();
        foreach (var (type, value) in items)
        {
            if (value.Length == 0)
            {
                output.Add(type);
                output.Add(0);
                continue;
            }
            for (var offset = 0; offset < value.Length; offset += 255)
            {
                var chunk = Math.Min(255, value.Length - offset);
                output.Add(type);
                output.Add((byte)chunk);
                output.AddRange(value.AsSpan(offset, chunk).ToArray());
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// Reads every item, joining split values back together. A later item of a type
    /// already seen (after a different type in between) replaces it.
    /// </summary>
    public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> data)
    {
        var items = new Dictionary<byte, byte[]>();
        var i = 0;
        while (i < data.Length)
        {
            if (i + 2 > data.Length)
                throw new InvalidDataException("TLV8 item is cut short.");

            var type = data[i];
            var value = new List<byte>();
            byte chunkLength;
            do
            {
                chunkLength = data[i + 1];
                if (i + 2 + chunkLength > data.Length)
                    throw new InvalidDataException("TLV8 value is cut short.");
                value.AddRange(data.Slice(i + 2, chunkLength).ToArray());
                i += 2 + chunkLength;
            }
            // A full 255-byte chunk followed by the same type continues the value
            while (chunkLength == 255 && i + 1 < data.Length && data[i] == type);

            items[type] = value.ToArray();
        }
        return items;
    }
}
