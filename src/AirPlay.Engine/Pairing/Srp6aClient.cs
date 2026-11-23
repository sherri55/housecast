using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// The client side of SRP-6a as HomeKit uses it. Ported from the csrp code in OwnTone's
/// pair_ap/pair_homekit.c (MIT): numbers are hashed in their shortest big-endian form,
/// except k and u, which pad both values to the group's length.
/// </summary>
public sealed class Srp6aClient
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

    private readonly HashAlgorithmName _hash;
    private readonly BigInteger _n;
    private readonly BigInteger _g;
    private readonly int _nLength;
    private readonly string _username;
    private readonly byte[] _password;
    private readonly BigInteger _a;
    private byte[] _expectedServerProof = [];

    /// <param name="privateA">The random secret a; only tests pass a fixed one.</param>
    public Srp6aClient(HashAlgorithmName hash, BigInteger n, BigInteger g, string username, string password, byte[]? privateA = null)
    {
        _hash = hash;
        _n = n;
        _g = g;
        _nLength = ToBytes(n).Length;
        _username = username;
        _password = Encoding.UTF8.GetBytes(password);
        _a = FromBytes(privateA ?? RandomNumberGenerator.GetBytes(32));
        PublicKey = ToBytes(BigInteger.ModPow(_g, _a, _n));
    }

    /// <summary>
    /// HomeKit pair-setup: SHA-512, the 3072-bit group, user "Pair-Setup", and the PIN as password.
    /// </summary>
    public static Srp6aClient ForHomeKit(string pin) => new(
        HashAlgorithmName.SHA512,
        BigInteger.Parse("0" + HomeKitPrimeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        new BigInteger(5),
        "Pair-Setup",
        pin);

    /// <summary>A = g^a mod N, sent to the receiver.</summary>
    public byte[] PublicKey { get; }

    /// <summary>K = H(S), the shared secret once both proofs check out.</summary>
    public byte[] SessionKey { get; private set; } = [];

    /// <summary>S, exposed so tests can compare it with RFC 5054.</summary>
    internal BigInteger PremasterSecret { get; private set; }

    /// <summary>
    /// Takes the receiver's salt and public key B, and returns the client proof M1.
    /// </summary>
    public byte[] ComputeProof(byte[] salt, byte[] serverPublicKey)
    {
        var s = FromBytes(salt);
        var b = FromBytes(serverPublicKey);
        var aBig = FromBytes(PublicKey);

        var k = FromBytes(Hash(Pad(_n), Pad(_g)));
        var u = FromBytes(Hash(Pad(aBig), Pad(b)));
        if (b % _n == 0 || u == 0)
            throw new CryptographicException("The receiver sent an unsafe SRP public key.");

        var inner = Hash(Encoding.UTF8.GetBytes(_username), ":"u8.ToArray(), _password);
        var x = FromBytes(Hash(ToBytes(s), inner));

        // S = (B - k·g^x)^(a + u·x) mod N, with the base kept positive
        var baseValue = ((b - k * BigInteger.ModPow(_g, x, _n)) % _n + _n) % _n;
        PremasterSecret = BigInteger.ModPow(baseValue, _a + u * x, _n);
        SessionKey = Hash(ToBytes(PremasterSecret));

        var hN = Hash(ToBytes(_n));
        var hG = Hash(ToBytes(_g));
        var hXor = hN.Zip(hG, (p, q) => (byte)(p ^ q)).ToArray();
        var proof = Hash(hXor, Hash(Encoding.UTF8.GetBytes(_username)), ToBytes(s), ToBytes(aBig), ToBytes(b), SessionKey);

        _expectedServerProof = Hash(ToBytes(aBig), proof, SessionKey);
        return proof;
    }

    /// <summary>Checks the receiver's proof M2, which shows it knew the PIN too.</summary>
    public bool VerifyServerProof(byte[] serverProof) =>
        CryptographicOperations.FixedTimeEquals(serverProof, _expectedServerProof);

    private byte[] Hash(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(_hash);
        foreach (var part in parts)
            hash.AppendData(part);
        return hash.GetHashAndReset();
    }

    private byte[] Pad(BigInteger value)
    {
        var bytes = ToBytes(value);
        var padded = new byte[_nLength];
        bytes.CopyTo(padded, _nLength - bytes.Length);
        return padded;
    }

    private static BigInteger FromBytes(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] ToBytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);
}
