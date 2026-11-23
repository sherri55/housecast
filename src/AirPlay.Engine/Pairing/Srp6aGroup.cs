using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// What both sides of SRP-6a share: the group, the hash, and how numbers are turned into
/// bytes. As in csrp (OwnTone's pair_ap, MIT), numbers are hashed in their shortest
/// big-endian form, except where k and u pad them to the group's length.
/// </summary>
internal sealed class Srp6aGroup(HashAlgorithmName hash, BigInteger n, BigInteger g)
{
    // RFC 5054's 3072-bit group with g = 5, as HomeKit uses
    private const string HomeKitPrimeHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B" +
        "139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485" +
        "B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1F" +
        "E649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23" +
        "DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32" +
        "905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF69558" +
        "17183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521" +
        "ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D7" +
        "1E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B1817" +
        "7B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82" +
        "D120A93AD2CAFFFFFFFFFFFFFFFF";

    /// <summary>HomeKit pair-setup: SHA-512 and the 3072-bit group.</summary>
    public static Srp6aGroup HomeKit { get; } = new(
        HashAlgorithmName.SHA512,
        BigInteger.Parse("0" + HomeKitPrimeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        new BigInteger(5));

    public BigInteger N { get; } = n;
    public BigInteger G { get; } = g;

    /// <summary>k = H(N | PAD(g)).</summary>
    public BigInteger K => FromBytes(Hash(Pad(N), Pad(G)));

    public byte[] Hash(params byte[][] parts)
    {
        using var incremental = IncrementalHash.CreateHash(hash);
        foreach (var part in parts)
            incremental.AppendData(part);
        return incremental.GetHashAndReset();
    }

    public byte[] Pad(BigInteger value)
    {
        var bytes = ToBytes(value);
        var padded = new byte[ToBytes(N).Length];
        bytes.CopyTo(padded, padded.Length - bytes.Length);
        return padded;
    }

    public static BigInteger FromBytes(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    public static byte[] ToBytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);
}
