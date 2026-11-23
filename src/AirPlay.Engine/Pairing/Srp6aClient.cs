using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// The client side of SRP-6a as HomeKit uses it. Ported from the csrp code in OwnTone's
/// pair_ap/pair_homekit.c (MIT).
/// </summary>
public sealed class Srp6aClient
{
    private readonly Srp6aGroup _group;
    private readonly string _username;
    private readonly byte[] _password;
    private readonly BigInteger _a;
    private byte[] _expectedServerProof = [];

    /// <param name="privateA">The random secret a; only tests pass a fixed one.</param>
    public Srp6aClient(HashAlgorithmName hash, BigInteger n, BigInteger g, string username, string password, byte[]? privateA = null)
        : this(new Srp6aGroup(hash, n, g), username, password, privateA)
    {
    }

    private Srp6aClient(Srp6aGroup group, string username, string password, byte[]? privateA)
    {
        _group = group;
        _username = username;
        _password = Encoding.UTF8.GetBytes(password);
        _a = Srp6aGroup.FromBytes(privateA ?? RandomNumberGenerator.GetBytes(32));
        PublicKey = Srp6aGroup.ToBytes(BigInteger.ModPow(group.G, _a, group.N));
    }

    /// <summary>
    /// HomeKit pair-setup: SHA-512, the 3072-bit group, user "Pair-Setup", and the PIN as password.
    /// </summary>
    public static Srp6aClient ForHomeKit(string pin) => new(Srp6aGroup.HomeKit, "Pair-Setup", pin, null);

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
        var (n, g) = (_group.N, _group.G);
        var s = Srp6aGroup.FromBytes(salt);
        var b = Srp6aGroup.FromBytes(serverPublicKey);
        var aBig = Srp6aGroup.FromBytes(PublicKey);

        var u = Srp6aGroup.FromBytes(_group.Hash(_group.Pad(aBig), _group.Pad(b)));
        if (b % n == 0 || u == 0)
            throw new CryptographicException("The receiver sent an unsafe SRP public key.");

        var inner = _group.Hash(Encoding.UTF8.GetBytes(_username), ":"u8.ToArray(), _password);
        var x = Srp6aGroup.FromBytes(_group.Hash(Srp6aGroup.ToBytes(s), inner));

        // S = (B - k·g^x)^(a + u·x) mod N, with the base kept positive
        var baseValue = ((b - _group.K * BigInteger.ModPow(g, x, n)) % n + n) % n;
        PremasterSecret = BigInteger.ModPow(baseValue, _a + u * x, n);
        SessionKey = _group.Hash(Srp6aGroup.ToBytes(PremasterSecret));

        var proof = Srp6aServer.ClientProof(_group, _username, s, aBig, b, SessionKey);
        _expectedServerProof = _group.Hash(Srp6aGroup.ToBytes(aBig), proof, SessionKey);
        return proof;
    }

    /// <summary>Checks the receiver's proof M2, which shows it knew the PIN too.</summary>
    public bool VerifyServerProof(byte[] serverProof) =>
        CryptographicOperations.FixedTimeEquals(serverProof, _expectedServerProof);
}
