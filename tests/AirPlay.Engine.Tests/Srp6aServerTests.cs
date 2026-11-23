using AirPlay.Engine.Pairing;

namespace AirPlay.Engine.Tests;

public class Srp6aServerTests
{
    [Fact]
    public void A_client_with_the_right_PIN_and_the_server_agree_on_the_key_and_both_proofs()
    {
        var server = Srp6aServer.ForHomeKit("3939");
        var client = Srp6aClient.ForHomeKit("3939");

        var clientProof = client.ComputeProof(server.Salt, server.PublicKey);
        var serverProof = server.VerifyClient(client.PublicKey, clientProof);

        Assert.NotNull(serverProof);
        Assert.True(client.VerifyServerProof(serverProof));
        Assert.Equal(client.SessionKey, server.SessionKey);
        Assert.Equal(64, server.SessionKey.Length);
    }

    [Fact]
    public void A_client_with_the_wrong_PIN_is_refused()
    {
        var server = Srp6aServer.ForHomeKit("3939");
        var client = Srp6aClient.ForHomeKit("1234");

        var clientProof = client.ComputeProof(server.Salt, server.PublicKey);

        Assert.Null(server.VerifyClient(client.PublicKey, clientProof));
        Assert.Empty(server.SessionKey);
    }
}
