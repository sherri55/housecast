namespace AirPlay.Engine.Pairing;

/// <summary>
/// What a PIN pairing leaves behind, needed to reconnect without a PIN.
/// </summary>
/// <param name="ClientPrivateKey">Our Ed25519 private key (the 32-byte seed).</param>
/// <param name="ReceiverId">The receiver's pairing identifier, as it sent it.</param>
/// <param name="ReceiverPublicKey">The receiver's long-term Ed25519 public key, to check its signature on reconnect.</param>
public sealed record PairingKeys(byte[] ClientPrivateKey, string ReceiverId, byte[] ReceiverPublicKey);
