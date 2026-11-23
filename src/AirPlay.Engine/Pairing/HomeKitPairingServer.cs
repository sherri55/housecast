using System.Text;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Security;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// The receiver side of HomeKit pairing, as an iPhone uses it with an AirPlay 2 receiver:
/// transient pair-setup (PIN 3939), which gives the shared secret that encrypts the
/// connection and the audio, and a pair-verify that is answered but changes nothing.
/// Ported from shairport-sync's rtsp.c and pair_ap (MIT).
/// </summary>
internal sealed class HomeKitPairingServer(string deviceId, byte[] longTermPrivateKey)
{
    private const string TransientPin = "3939";
    private const byte FlagsTransient = 0x10;
    private const byte ErrorAuthentication = 2;

    private Srp6aServer? _srp;

    /// <summary>Set once transient pair-setup succeeds; everything after it is encrypted.</summary>
    public byte[]? SharedSecret { get; private set; }

    /// <summary>Answers one /pair-setup message.</summary>
    public byte[] PairSetup(byte[] request)
    {
        var items = Tlv8.Decode(request);
        switch (State(items))
        {
            case 1:
                // Only transient pairing is offered: there are no long-term keys to set up
                if (!items.TryGetValue(Tlv8.Flags, out var flags) || (flags.FirstOrDefault() & FlagsTransient) == 0)
                    return Tlv8.Encode((Tlv8.State, [2]), (Tlv8.Error, [ErrorAuthentication]));
                _srp = Srp6aServer.ForHomeKit(TransientPin);
                return Tlv8.Encode((Tlv8.State, [2]), (Tlv8.Salt, _srp.Salt), (Tlv8.PublicKey, _srp.PublicKey));

            case 3 when _srp is not null && items.TryGetValue(Tlv8.PublicKey, out var a) && items.TryGetValue(Tlv8.Proof, out var m1):
                var proof = _srp.VerifyClient(a, m1);
                if (proof is null)
                    return Tlv8.Encode((Tlv8.State, [4]), (Tlv8.Error, [ErrorAuthentication]));
                SharedSecret = _srp.SessionKey;
                return Tlv8.Encode((Tlv8.State, [4]), (Tlv8.Proof, proof));

            default:
                return Tlv8.Encode((Tlv8.State, [(byte)(State(items) + 1)]), (Tlv8.Error, [ErrorAuthentication]));
        }
    }

    /// <summary>
    /// Answers one /pair-verify message: proves we hold the key in our <c>pk</c> TXT record.
    /// As in shairport-sync, it doesn't change the connection's encryption.
    /// </summary>
    public byte[] PairVerify(byte[] request)
    {
        var items = Tlv8.Decode(request);
        if (State(items) != 1 || !items.TryGetValue(Tlv8.PublicKey, out var senderEphemeral))
            return Tlv8.Encode((Tlv8.State, [4]));

        var ephemeralPrivate = new byte[X25519.ScalarSize];
        X25519.GeneratePrivateKey(new SecureRandom(), ephemeralPrivate);
        var ephemeralPublic = new byte[X25519.PointSize];
        X25519.GeneratePublicKey(ephemeralPrivate, 0, ephemeralPublic, 0);
        var shared = new byte[X25519.PointSize];
        X25519.CalculateAgreement(ephemeralPrivate, 0, senderEphemeral, 0, shared, 0);

        var id = Encoding.UTF8.GetBytes(deviceId);
        var signature = HomeKitPairing.Sign(longTermPrivateKey, [.. ephemeralPublic, .. id, .. senderEphemeral]);
        var verifyKey = HomeKitPairing.Derive(shared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
        var sealedM2 = HomeKitPairing.Seal(verifyKey, "PV-Msg02", Tlv8.Encode((Tlv8.Identifier, id), (Tlv8.Signature, signature)));
        return Tlv8.Encode((Tlv8.State, [2]), (Tlv8.PublicKey, ephemeralPublic), (Tlv8.EncryptedData, sealedM2));
    }

    /// <summary>The connection's keys as the receiver uses them: the sender's read key is our write key.</summary>
    public static (byte[] Write, byte[] Read) ControlChannelKeys(byte[] sharedSecret) =>
        (HomeKitPairing.Derive(sharedSecret, "Control-Salt", "Control-Read-Encryption-Key"),
         HomeKitPairing.Derive(sharedSecret, "Control-Salt", "Control-Write-Encryption-Key"));

    private static int State(Dictionary<byte, byte[]> items) =>
        items.TryGetValue(Tlv8.State, out var state) ? state.FirstOrDefault() : 0;
}
