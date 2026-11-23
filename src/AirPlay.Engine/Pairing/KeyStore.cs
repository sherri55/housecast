using System.Security.Cryptography;
using System.Text.Json;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// Keeps our pairing identity, the keys from PIN pairings, and the long-term keys of the
/// receivers we announce, in one file encrypted for the current Windows user with DPAPI.
/// </summary>
public sealed class KeyStore
{
    private sealed record Contents(string ClientId, Dictionary<string, PairingKeys> Pairings)
    {
        // Files from before receivers existed don't have this
        public Dictionary<string, byte[]> ReceiverKeys { get; init; } = [];
    }

    private readonly string _path;
    private Contents _contents;

    public KeyStore(string path)
    {
        _path = path;
        _contents = Load(path) ?? new Contents(NewClientId(), []);
    }

    /// <summary>
    /// Our identifier in pairings: 16 hex digits, created once and kept, because receivers
    /// remember pairings against it.
    /// </summary>
    public string ClientId => _contents.ClientId;

    /// <param name="speakerId">The speaker's announced device id.</param>
    public PairingKeys? Find(string speakerId) => _contents.Pairings.GetValueOrDefault(speakerId);

    public void Save(string speakerId, PairingKeys keys)
    {
        _contents.Pairings[speakerId] = keys;
        Write();
    }

    public void Forget(string speakerId)
    {
        if (_contents.Pairings.Remove(speakerId))
            Write();
    }

    /// <summary>
    /// The Ed25519 private key of a receiver we announce, created the first time it's asked
    /// for, so the receiver's public key (its <c>pk</c>) stays the same from run to run.
    /// </summary>
    /// <param name="deviceId">The receiver's announced device id.</param>
    public byte[] ReceiverKey(string deviceId)
    {
        if (_contents.ReceiverKeys.TryGetValue(deviceId, out var key))
            return key;
        key = RandomNumberGenerator.GetBytes(32);
        _contents.ReceiverKeys[deviceId] = key;
        Write();
        return key;
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(_contents);
        File.WriteAllBytes(_path, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
    }

    private static Contents? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        var json = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Contents>(json);
    }

    private static string NewClientId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
}
