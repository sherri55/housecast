using System.Net;
using System.Net.Sockets;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Ptp;
using AirPlay.Engine.Receiver;
using AirPlay.Engine.Rtsp;
using Claunia.PropertyList;

namespace AirPlay.Engine.Tests;

public class ReceiverSessionTests
{
    [Fact]
    public async Task Our_sender_reads_info_pairs_transiently_and_then_talks_encrypted()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"receiver-test-{Guid.NewGuid():N}.bin");
        try
        {
            var identity = new ReceiverIdentity("Upper Level", new KeyStore(keyFile));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync();

            await using var sender = new RtspClient(1, "Test");
            await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            await using var session = new ReceiverSession(identity, await accept, new PtpFollower(), TimeSpan.Zero, _ => { });
            var running = session.RunAsync();

            var plain = await sender.SendAsync("GET", "/info");
            await HomeKitPairing.PairTransientAsync(sender);
            var encrypted = await sender.SendAsync("GET", "/info");

            Assert.Equal("Upper Level", ((NSString)((NSDictionary)PropertyListParser.Parse(plain.Body))["name"]).Content);
            Assert.True(encrypted.IsSuccess);
            Assert.Equal(identity.DeviceId, ((NSString)((NSDictionary)PropertyListParser.Parse(encrypted.Body))["deviceID"]).Content);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void A_group_keeps_its_device_id_and_key_from_run_to_run()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"receiver-test-{Guid.NewGuid():N}.bin");
        try
        {
            var first = new ReceiverIdentity("Upper Level", new KeyStore(keyFile));
            var again = new ReceiverIdentity("Upper Level", new KeyStore(keyFile));
            var other = new ReceiverIdentity("Downstairs", new KeyStore(keyFile));

            Assert.Equal(first.DeviceId, again.DeviceId);
            Assert.Equal(first.PublicKey, again.PublicKey);
            Assert.NotEqual(first.DeviceId, other.DeviceId);
            // Locally administered, unicast
            Assert.Equal(0x02, first.DeviceIdBytes[0] & 0x03);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void FairPlay_setup_answers_like_shairport_sync()
    {
        var first = new byte[16];
        first[4] = 3;
        first[5] = 1;
        first[6] = 1;
        first[14] = 2;
        var second = new byte[164];
        second[4] = 3;
        second[5] = 1;
        second[6] = 3;
        Random.Shared.NextBytes(second.AsSpan(144));

        var firstReply = FairPlaySetup.Reply(first)!;
        var secondReply = FairPlaySetup.Reply(second)!;

        Assert.Equal(142, firstReply.Length);
        // The reply for mode 2 carries the mode at offset 13
        Assert.Equal(2, firstReply[13]);
        Assert.Equal(32, secondReply.Length);
        Assert.Equal(second[^20..], secondReply[12..]);
        Assert.Null(FairPlaySetup.Reply([1, 2, 3]));
    }
}
