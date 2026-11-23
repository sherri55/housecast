using AirPlay.Engine.Pairing;

namespace AirPlay.Engine.Tests;

public class Tlv8Tests
{
    [Fact]
    public void Short_values_are_type_length_value()
    {
        var bytes = Tlv8.Encode((Tlv8.State, [1]), (Tlv8.Method, [0]), (Tlv8.Flags, [0x10]));

        Assert.Equal(new byte[] { 6, 1, 1, 0, 1, 0, 19, 1, 0x10 }, bytes);
    }

    [Fact]
    public void Long_values_are_split_into_255_byte_chunks_and_joined_again()
    {
        var key = Enumerable.Range(0, 384).Select(i => (byte)i).ToArray();

        var bytes = Tlv8.Encode((Tlv8.State, [3]), (Tlv8.PublicKey, key), (Tlv8.Proof, new byte[64]));
        var items = Tlv8.Decode(bytes);

        // 3 + (2 + 255) + (2 + 129) + (2 + 64)
        Assert.Equal(457, bytes.Length);
        Assert.Equal(key, items[Tlv8.PublicKey]);
        Assert.Equal(new byte[] { 3 }, items[Tlv8.State]);
        Assert.Equal(64, items[Tlv8.Proof].Length);
    }

    [Fact]
    public void Empty_values_round_trip()
    {
        var items = Tlv8.Decode(Tlv8.Encode((Tlv8.Identifier, [])));

        Assert.Empty(items[Tlv8.Identifier]);
    }

    [Fact]
    public void Cut_short_data_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => Tlv8.Decode(new byte[] { 3, 10, 1, 2 }));
}
