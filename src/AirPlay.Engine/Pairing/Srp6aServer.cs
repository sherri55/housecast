using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// The receiver side of SRP-6a as HomeKit uses it, the counterpart of <see cref="Srp6aClient"/>.
/// Ported from the csrp server code in shairport-sync's pair_ap (MIT).
/// </summary>
internal sealed class Srp6aServer
{
    private const string Username = "Pair-Setup";

    private readonly Srp6aGroup _group;
    private readonly BigInteger _verifier;
    private readonly BigInteger _b;

    /// <param name="privateB">The random secret b; only tests pass a fixed one.</param>
    public Srp6aServer(Srp6aGroup group, string pin, byte[]? salt = null, byte[]? privateB = null)
    {
        _group = group;
        Salt = salt ?? RandomNumberGenerator.GetBytes(16);
        _b = Srp6aGroup.FromBytes(privateB ?? RandomNumberGenerator.GetBytes(32));

        // v = g^x, with x = H(s | H(user:pin))
        var inner = group.Hash(Encoding.UTF8.GetBytes(Username), ":"u8.ToArray(), Encoding.UTF8.GetBytes(pin));
        var x = Srp6aGroup.FromBytes(group.Hash(Salt, inner));
        _verifier = BigInteger.ModPow(group.G, x, group.N);

        // B = k·v + g^b mod N
        PublicKey = Srp6aGroup.ToBytes((group.K * _verifier + BigInteger.ModPow(group.G, _b, group.N)) % group.N);
    }

    public static Srp6aServer ForHomeKit(string pin) => new(Srp6aGroup.HomeKit, pin);

    public byte[] Salt { get; }

    /// <summary>B, sent to the sender with the salt.</summary>
    public byte[] PublicKey { get; }

    /// <summary>K = H(S), the shared secret once the sender's proof checks out.</summary>
    public byte[] SessionKey { get; private set; } = [];

    /// <summary>
    /// Takes the sender's public key A and proof M1. Returns our proof M2, or null if the
    /// sender doesn't know the PIN.
    /// </summary>
    public byte[]? VerifyClient(byte[] clientPublicKey, byte[] clientProof)
    {
        var (n, g) = (_group.N, _group.G);
        var a = Srp6aGroup.FromBytes(clientPublicKey);
        if (a % n == 0)
            return null;
        var b = Srp6aGroup.FromBytes(PublicKey);
        var u = Srp6aGroup.FromBytes(_group.Hash(_group.Pad(a), _group.Pad(b)));

        // S = (A·v^u)^b mod N
        var premaster = BigInteger.ModPow(a * BigInteger.ModPow(_verifier, u, n) % n, _b, n);
        var sessionKey = _group.Hash(Srp6aGroup.ToBytes(premaster));

        var expected = ClientProof(_group, Username, Srp6aGroup.FromBytes(Salt), a, b, sessionKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, clientProof))
            return null;

        SessionKey = sessionKey;
        return _group.Hash(Srp6aGroup.ToBytes(a), clientProof, sessionKey);
    }

    /// <summary>M1 = H(H(N) xor H(g) | H(user) | s | A | B | K), which both sides compute.</summary>
    internal static byte[] ClientProof(Srp6aGroup group, string username, BigInteger salt, BigInteger a, BigInteger b, byte[] sessionKey)
    {
        var hN = group.Hash(Srp6aGroup.ToBytes(group.N));
        var hG = group.Hash(Srp6aGroup.ToBytes(group.G));
        var hXor = hN.Zip(hG, (p, q) => (byte)(p ^ q)).ToArray();
        return group.Hash(hXor, group.Hash(Encoding.UTF8.GetBytes(username)), Srp6aGroup.ToBytes(salt), Srp6aGroup.ToBytes(a), Srp6aGroup.ToBytes(b), sessionKey);
    }
}
