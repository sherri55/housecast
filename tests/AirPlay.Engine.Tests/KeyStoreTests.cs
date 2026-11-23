using System.Text;
using AirPlay.Engine.Pairing;

namespace AirPlay.Engine.Tests;

public class KeyStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"keystore-test-{Guid.NewGuid():N}.bin");

    [Fact]
    public void Saved_keys_and_the_client_id_survive_a_reload()
    {
        var keys = new PairingKeys([1, 2, 3], "TV-ID", [4, 5, 6]);
        var store = new KeyStore(_path);
        store.Save("60:AB:D2:F6:6A:D0", keys);

        var reloaded = new KeyStore(_path);

        Assert.Equal(store.ClientId, reloaded.ClientId);
        Assert.Matches("^[0-9A-F]{16}$", reloaded.ClientId);
        var found = reloaded.Find("60:AB:D2:F6:6A:D0")!;
        Assert.Equal(keys.ClientPrivateKey, found.ClientPrivateKey);
        Assert.Equal("TV-ID", found.ReceiverId);
        Assert.Equal(keys.ReceiverPublicKey, found.ReceiverPublicKey);
    }

    [Fact]
    public void The_file_is_not_readable_as_plain_text()
    {
        new KeyStore(_path).Save("speaker", new PairingKeys([9], "VISIBLE-RECEIVER-ID", [9]));

        Assert.DoesNotContain("VISIBLE-RECEIVER-ID", Encoding.UTF8.GetString(File.ReadAllBytes(_path)));
    }

    [Fact]
    public void Forgotten_keys_are_gone_after_a_reload()
    {
        var store = new KeyStore(_path);
        store.Save("speaker", new PairingKeys([1], "id", [1]));
        store.Forget("speaker");

        Assert.Null(new KeyStore(_path).Find("speaker"));
    }

    public void Dispose() => File.Delete(_path);
}
