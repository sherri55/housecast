using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Engine.Pairing;

namespace AirPlay.Engine.Tests;

public class Srp6aClientTests
{
    // RFC 5054 appendix B: SHA-1, the 1024-bit group, g = 2
    private static readonly BigInteger N = Hex(
        "EEAF0AB9ADB38DD69C33F80AFA8FC5E86072618775FF3C0B9EA2314C9C256576D674DF7496EA81D3383B4813D692C6E0E0D5D8E250B98BE4" +
        "8E495C1D6089DAD15DC7D7B46154D6B6CE8EF4AD69B15D4982559B297BCF1885C529F566660E57EC68EDBC3C05726CC02FD4CBF4976EAA9A" +
        "FD5138FE8376435B9FC61D2FC0EB06E3");
    private static readonly byte[] Salt = Convert.FromHexString("BEB25379D1A8581EB5A727673A2441EE");
    private static readonly byte[] PrivateA = Convert.FromHexString("60975527035CF2AD1989806F0407210BC81EDC04E2762A56AFD529DDDA2D4393");
    private static readonly BigInteger PrivateB = Hex("E487CB59D31AC550471E81F00F6928E01DDA08E974A004F49E61F5D105284D20");

    [Fact]
    public void Public_key_A_matches_RFC_5054()
    {
        var client = new Srp6aClient(HashAlgorithmName.SHA1, N, 2, "alice", "password123", PrivateA);

        Assert.Equal(
            "61D5E490F6F1B79547B0704C436F523DD0E560F0C64115BB72557EC44352E8903211C04692272D8B2D1A5358A2CF1B6E0BFCF99F921530EC" +
            "8E39356179EAE45E42BA92AEACED825171E1E8B9AF6D9C03E1327F44BE087EF06530E69F66615261EEF54073CA11CF5858F0EDFDFE15EFEA" +
            "B349EF5D76988A3672FAC47B0769447B",
            Convert.ToHexString(client.PublicKey));
    }

    [Fact]
    public void Premaster_secret_S_matches_RFC_5054()
    {
        var client = new Srp6aClient(HashAlgorithmName.SHA1, N, 2, "alice", "password123", PrivateA);
        var b = Convert.FromHexString(
            "BD0C61512C692C0CB6D041FA01BB152D4916A1E77AF46AE105393011BAF38964DC46A0670DD125B95A981652236F99D9B681CBF87837EC99" +
            "6C6DA04453728610D0C6DDB58B318885D7D82C7F8DEB75CE7BD4FBAA37089E6F9C6059F388838E7A00030B331EB76840910440B1B27AAEAE" +
            "EB4012B7D7665238A8E3FB004B117B58");

        client.ComputeProof(Salt, b);

        Assert.Equal(
            "B0DC82BABCF30674AE450C0287745E7990A3381F63B387AAF271A10D233861E359B48220F7C4693C9AE12B0A6F67809F0876E2D013800D6C" +
            "41BB59B6D5979B5C00A172B4A2A5903A0BDCAF8A709585EB2AFAFA8F3499B200210DCC1F10EB33943CD67FC88A2F39A4BE5BEC4EC0A3212D" +
            "C346D7E474B29EDE8A469FFECA686E5A",
            Convert.ToHexString(client.PremasterSecret.ToByteArray(isUnsigned: true, isBigEndian: true)));
    }

    [Fact]
    public void A_server_with_the_same_PIN_reaches_the_same_secret_and_accepts_the_proofs()
    {
        // Plays the receiver's side of HomeKit SRP-6a, so the 3072-bit SHA-512 path is checked end to end
        var client = Srp6aClient.ForHomeKit("3939");
        var homeKitN = BigInteger.Parse(
            "0FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485" +
            "B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23" +
            "DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF69558" +
            "17183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D7" +
            "1E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82" +
            "D120A93AD2CAFFFFFFFFFFFFFFFF", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = new BigInteger(5);
        var salt = RandomNumberGenerator.GetBytes(16);
        var b = FromBytes(RandomNumberGenerator.GetBytes(32));

        var x = FromBytes(Sha512(salt, Sha512(Encoding.UTF8.GetBytes("Pair-Setup:3939"))));
        var v = BigInteger.ModPow(g, x, homeKitN);
        var k = FromBytes(Sha512(Pad(homeKitN, homeKitN), Pad(g, homeKitN)));
        var serverPublic = (k * v + BigInteger.ModPow(g, b, homeKitN)) % homeKitN;

        var proof = client.ComputeProof(salt, ToBytes(serverPublic));

        var a = FromBytes(client.PublicKey);
        var u = FromBytes(Sha512(Pad(a, homeKitN), Pad(serverPublic, homeKitN)));
        var serverS = BigInteger.ModPow(a * BigInteger.ModPow(v, u, homeKitN) % homeKitN, b, homeKitN);
        var serverK = Sha512(ToBytes(serverS));
        var serverProof = Sha512(client.PublicKey, proof, serverK);

        Assert.Equal(serverK, client.SessionKey);
        Assert.Equal(64, proof.Length);
        Assert.True(client.VerifyServerProof(serverProof));
        Assert.False(client.VerifyServerProof(new byte[64]));
    }

    private static BigInteger Hex(string hex) => BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static BigInteger FromBytes(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] ToBytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static byte[] Pad(BigInteger value, BigInteger n)
    {
        var bytes = ToBytes(value);
        var padded = new byte[ToBytes(n).Length];
        bytes.CopyTo(padded, padded.Length - bytes.Length);
        return padded;
    }

    private static byte[] Sha512(params byte[][] parts) => SHA512.HashData(parts.SelectMany(p => p).ToArray());
}
