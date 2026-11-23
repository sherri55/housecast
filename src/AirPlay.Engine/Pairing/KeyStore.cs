using System.Security.Cryptography;
using System.Text.Json;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// Keeps our pairing identity and the keys from PIN pairings, in one file encrypted
/// for the current Windows user with DPAPI.
/// </summary>
public sealed class KeyStore
{
    private sealed record Contents(string ClientId, Dictionary<string, PairingKeys> Pairings);

    private readonly string _path;
    private Contents _contents;

    public KeyStore(string path)
    {
        _path = path;
        _contents = Load(path) ?? new Contents(NewClientId(), []);
    }

    /// <summary>
    /// The default file: %APPDATA%\AirPlayTray\pairings.bin.
    /// </summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirPlayTray", "pairings.bin");

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
