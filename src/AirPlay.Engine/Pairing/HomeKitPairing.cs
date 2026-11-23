using System.Security.Cryptography;
using System.Text;
using AirPlay.Engine.Rtsp;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// HomeKit-style pairing with an AirPlay 2 receiver over its RTSP connection.
/// Ported from the client side of OwnTone's pair_ap/pair_homekit.c (MIT).
/// </summary>
public static class HomeKitPairing
{
    private const string TransientPin = "3939";
    private const byte MethodPairSetup = 0;
    private const byte FlagsTransient = 0x10;

    // X-Apple-HKP tells the receiver which pairing this is: 4 transient, 3 HomeKit with a PIN
    private static readonly Dictionary<string, string> TransientHeaders = new() { ["X-Apple-HKP"] = "4" };
    private static readonly Dictionary<string, string> PinHeaders = new() { ["X-Apple-HKP"] = "3" };

    /// <summary>
    /// Transient pairing: SRP with the fixed PIN 3939 and no stored keys. Most speakers
    /// accept it. Afterwards every request on the connection is encrypted.
    /// </summary>
    /// <returns>The shared secret, which later keys (such as the audio key) derive from.</returns>
    public static async Task<byte[]> PairTransientAsync(RtspClient rtsp, CancellationToken cancellationToken = default)
    {
        var srp = Srp6aClient.ForHomeKit(TransientPin);

        var m2 = await ExchangeAsync(rtsp, "/pair-setup", TransientHeaders, 2, cancellationToken,
            (Tlv8.State, [1]), (Tlv8.Method, [MethodPairSetup]), (Tlv8.Flags, [FlagsTransient]));
        var proof = srp.ComputeProof(Require(m2, Tlv8.Salt, "salt"), Require(m2, Tlv8.PublicKey, "public key"));

        var m4 = await ExchangeAsync(rtsp, "/pair-setup", TransientHeaders, 4, cancellationToken,
            (Tlv8.State, [3]), (Tlv8.PublicKey, srp.PublicKey), (Tlv8.Proof, proof));
        if (!srp.VerifyServerProof(Require(m4, Tlv8.Proof, "proof")))
            throw new PairingException("The receiver's proof didn't check out.");

        UseEncryption(rtsp, srp.SessionKey);
        return srp.SessionKey;
    }

    /// <summary>
    /// Asks the receiver (typically a TV) to show a PIN. Pair with <see cref="PairWithPinAsync"/>
    /// on the same connection: some receivers only accept the PIN on the connection that asked for it.
    /// </summary>
    public static async Task RequestPinAsync(RtspClient rtsp, CancellationToken cancellationToken = default)
    {
        var reply = await rtsp.SendAsync("POST", "/pair-pin-start", headers: PinHeaders, cancellationToken: cancellationToken);
        if (!reply.IsSuccess)
            throw new PairingException($"The receiver wouldn't show a PIN: {reply.StatusCode} {reply.Reason}.");
    }

    /// <summary>
    /// Pairs with the PIN the receiver shows, exchanging long-term keys so later connections
    /// can use <see cref="VerifyAsync"/> instead of a PIN.
    /// </summary>
    /// <param name="clientId">Our pairing identifier; it must stay the same for the keys to keep working.</param>
    public static async Task<PairingKeys> PairWithPinAsync(RtspClient rtsp, string pin, string clientId, CancellationToken cancellationToken = default)
    {
        var srp = Srp6aClient.ForHomeKit(pin);

        var m2 = await ExchangeAsync(rtsp, "/pair-setup", PinHeaders, 2, cancellationToken,
            (Tlv8.State, [1]), (Tlv8.Method, [MethodPairSetup]));
        var proof = srp.ComputeProof(Require(m2, Tlv8.Salt, "salt"), Require(m2, Tlv8.PublicKey, "public key"));

        var m4 = await ExchangeAsync(rtsp, "/pair-setup", PinHeaders, 4, cancellationToken,
            (Tlv8.State, [3]), (Tlv8.PublicKey, srp.PublicKey), (Tlv8.Proof, proof));
        if (!srp.VerifyServerProof(Require(m4, Tlv8.Proof, "proof")))
            throw new PairingException("The PIN wasn't accepted.");

        // M5: our long-term public key, signed over (derived x, our id, our key), sent encrypted
        var clientPrivateKey = RandomNumberGenerator.GetBytes(Ed25519.SecretKeySize);
        var clientPublicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(clientPrivateKey, 0, clientPublicKey, 0);
        var clientIdBytes = Encoding.UTF8.GetBytes(clientId);

        var controllerX = Derive(srp.SessionKey, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info");
        var signature = Sign(clientPrivateKey, [.. controllerX, .. clientIdBytes, .. clientPublicKey]);
        var setupKey = Derive(srp.SessionKey, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
        var sealedM5 = Seal(setupKey, "PS-Msg05", Tlv8.Encode(
            (Tlv8.Identifier, clientIdBytes), (Tlv8.Signature, signature), (Tlv8.PublicKey, clientPublicKey)));

        var m6 = await ExchangeAsync(rtsp, "/pair-setup", PinHeaders, 6, cancellationToken,
            (Tlv8.State, [5]), (Tlv8.EncryptedData, sealedM5));

        // M6: the receiver's long-term key, signed the same way
        var receiver = Tlv8.Decode(Open(setupKey, "PS-Msg06", Require(m6, Tlv8.EncryptedData, "encrypted data")));
        var receiverId = Require(receiver, Tlv8.Identifier, "identifier");
        var receiverPublicKey = Require(receiver, Tlv8.PublicKey, "public key");
        var accessoryX = Derive(srp.SessionKey, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
        if (!Verify(receiverPublicKey, Require(receiver, Tlv8.Signature, "signature"), [.. accessoryX, .. receiverId, .. receiverPublicKey]))
            throw new PairingException("The receiver's signature didn't check out.");

        return new PairingKeys(clientPrivateKey, Encoding.UTF8.GetString(receiverId), receiverPublicKey);
    }

    /// <summary>
    /// Reconnects to a receiver paired earlier with a PIN, proving both sides hold the keys
    /// from that pairing. Afterwards every request on the connection is encrypted.
    /// </summary>
    /// <returns>The shared secret for this connection.</returns>
    public static async Task<byte[]> VerifyAsync(RtspClient rtsp, PairingKeys keys, string clientId, CancellationToken cancellationToken = default)
    {
        var ephemeralPrivate = new byte[X25519.ScalarSize];
        X25519.GeneratePrivateKey(new SecureRandom(), ephemeralPrivate);
        var ephemeralPublic = new byte[X25519.PointSize];
        X25519.GeneratePublicKey(ephemeralPrivate, 0, ephemeralPublic, 0);

        var m2 = await ExchangeAsync(rtsp, "/pair-verify", PinHeaders, 2, cancellationToken,
            (Tlv8.State, [1]), (Tlv8.PublicKey, ephemeralPublic));

        var receiverEphemeral = Require(m2, Tlv8.PublicKey, "public key");
        var sharedSecret = new byte[X25519.PointSize];
        X25519.CalculateAgreement(ephemeralPrivate, 0, receiverEphemeral, 0, sharedSecret, 0);

        // M2: the receiver signs (its ephemeral key, its id, our ephemeral key) with its long-term key
        var verifyKey = Derive(sharedSecret, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
        var receiver = Tlv8.Decode(Open(verifyKey, "PV-Msg02", Require(m2, Tlv8.EncryptedData, "encrypted data")));
        var receiverId = Require(receiver, Tlv8.Identifier, "identifier");
        if (!Verify(keys.ReceiverPublicKey, Require(receiver, Tlv8.Signature, "signature"), [.. receiverEphemeral, .. receiverId, .. ephemeralPublic]))
            throw new PairingException("The receiver isn't the one paired earlier.");

        // M3: we sign (our ephemeral key, our id, its ephemeral key) with our long-term key
        var clientIdBytes = Encoding.UTF8.GetBytes(clientId);
        var signature = Sign(keys.ClientPrivateKey, [.. ephemeralPublic, .. clientIdBytes, .. receiverEphemeral]);
        var sealedM3 = Seal(verifyKey, "PV-Msg03", Tlv8.Encode((Tlv8.Identifier, clientIdBytes), (Tlv8.Signature, signature)));

        await ExchangeAsync(rtsp, "/pair-verify", PinHeaders, 4, cancellationToken,
            (Tlv8.State, [3]), (Tlv8.EncryptedData, sealedM3));

        UseEncryption(rtsp, sharedSecret);
        return sharedSecret;
    }

    /// <summary>
    /// The keys for the RTSP connection after pairing, as the client (sender) uses them.
    /// </summary>
    public static (byte[] Write, byte[] Read) ControlChannelKeys(byte[] sharedSecret) =>
        (Derive(sharedSecret, "Control-Salt", "Control-Write-Encryption-Key"),
         Derive(sharedSecret, "Control-Salt", "Control-Read-Encryption-Key"));

    private static void UseEncryption(RtspClient rtsp, byte[] sharedSecret)
    {
        var (writeKey, readKey) = ControlChannelKeys(sharedSecret);
        rtsp.UseStream(stream => new EncryptedChannelStream(stream, writeKey, readKey));
    }

    private static byte[] Derive(byte[] secret, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info));

    private static byte[] Sign(byte[] privateKey, byte[] message)
    {
        var signature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(privateKey, 0, message, 0, message.Length, signature, 0);
        return signature;
    }

    private static bool Verify(byte[] publicKey, byte[] signature, byte[] message) =>
        signature.Length == Ed25519.SignatureSize && Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);

    // Pairing messages encrypt a TLV with a fixed per-message nonce: 4 zero bytes and an 8-byte label
    private static byte[] Seal(byte[] key, string nonceLabel, byte[] plain)
    {
        using var cipher = new ChaCha20Poly1305(key);
        var output = new byte[plain.Length + 16];
        cipher.Encrypt(Nonce(nonceLabel), plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length));
        return output;
    }

    private static byte[] Open(byte[] key, string nonceLabel, byte[] sealedData)
    {
        if (sealedData.Length < 16)
            throw new PairingException("The receiver's encrypted reply is too short.");
        using var cipher = new ChaCha20Poly1305(key);
        var plain = new byte[sealedData.Length - 16];
        cipher.Decrypt(Nonce(nonceLabel), sealedData.AsSpan(0, plain.Length), sealedData.AsSpan(plain.Length), plain);
        return plain;
    }

    private static byte[] Nonce(string label) => [0, 0, 0, 0, .. Encoding.ASCII.GetBytes(label)];

    // Sends one pairing message and returns the reply's TLV items, after checking its state and error
    private static async Task<Dictionary<byte, byte[]>> ExchangeAsync(
        RtspClient rtsp,
        string path,
        IReadOnlyDictionary<string, string> headers,
        byte expectedState,
        CancellationToken cancellationToken,
        params (byte Type, byte[] Value)[] request)
    {
        var step = $"{path.TrimStart('/')} step {expectedState / 2}";
        var reply = await rtsp.SendAsync("POST", path, Tlv8.Encode(request), "application/octet-stream", headers, cancellationToken);
        if (!reply.IsSuccess)
            throw new PairingException($"{step} was refused: {reply.StatusCode} {reply.Reason}.");

        var items = Tlv8.Decode(reply.Body);
        if (items.TryGetValue(Tlv8.Error, out var error))
            throw new PairingException($"{step} failed with error {error.FirstOrDefault()}.");
        if (!items.TryGetValue(Tlv8.State, out var state) || state.FirstOrDefault() != expectedState)
            throw new PairingException($"{step} came back out of order.");
        return items;
    }

    private static byte[] Require(Dictionary<byte, byte[]> items, byte type, string what) =>
        items.TryGetValue(type, out var value) ? value : throw new PairingException($"The receiver's reply had no {what}.");
}

public sealed class PairingException(string message) : Exception(message);
