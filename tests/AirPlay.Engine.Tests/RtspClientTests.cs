using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlay.Engine.Rtsp;

namespace AirPlay.Engine.Tests;

public class RtspClientTests
{
    [Fact]
    public async Task Sends_the_AirPlay_headers_and_reads_a_binary_reply()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] replyBody = [0x62, 0x70, 0x6C, 0x00, 0xFF, 0x0D, 0x0A, 0x01];

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var request = await ReadRequestAsync(stream);
            var head = Encoding.ASCII.GetBytes(
                $"RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Type: application/x-apple-binary-plist\r\nContent-Length: {replyBody.Length}\r\n\r\n");
            await stream.WriteAsync(head.Concat(replyBody).ToArray());
            return request;
        });

        await using var client = new RtspClient(0x0123456789ABCDEF, "Test");
        await client.ConnectAsync(IPAddress.Loopback, port);
        var reply = await client.SendAsync("GET", "/info");
        var request = await server;

        Assert.StartsWith("GET /info RTSP/1.0\r\n", request);
        Assert.Contains("CSeq: 1\r\n", request);
        Assert.Contains("User-Agent: AirPlay/999.0.0\r\n", request);
        Assert.Contains("DACP-ID: 0123456789ABCDEF\r\n", request);
        Assert.Contains($"Active-Remote: {client.ActiveRemote}\r\n", request);
        Assert.Contains("Content-Length: 0\r\n", request);
        Assert.Equal(200, reply.StatusCode);
        Assert.Equal("application/x-apple-binary-plist", reply.Headers["content-type"]);
        Assert.Equal(replyBody, reply.Body);
    }

    [Fact]
    public async Task Sends_a_body_with_its_type_and_counts_CSeq_up()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var requests = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                requests.Add(await ReadRequestAsync(stream));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nContent-Length: 0\r\n\r\n"));
            }
            return requests;
        });

        await using var client = new RtspClient(1, "Test");
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.SendAsync("GET", "/info");
        await client.SendAsync("SET_PARAMETER", "rtsp://x/1", Encoding.ASCII.GetBytes("volume: -15.0\r\n"), "text/parameters");
        var requests = await server;

        Assert.Contains("CSeq: 2\r\n", requests[1]);
        Assert.Contains("Content-Type: text/parameters\r\n", requests[1]);
        Assert.EndsWith("Content-Length: 15\r\n\r\nvolume: -15.0\r\n", requests[1]);
    }

    [Fact]
    public async Task Gives_its_local_address_in_plain_IPv4_form()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();

        await using var client = new RtspClient(1, "Test");
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accept;

        Assert.Equal(IPAddress.Loopback, client.LocalEndPoint!.Address);
    }

    // Reads one request: the header block, then as many body bytes as Content-Length says
    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n"))
        {
            await stream.ReadExactlyAsync(one);
            bytes.Add(one[0]);
        }
        var head = Encoding.ASCII.GetString(bytes.ToArray());
        var lengthLine = head.Split("\r\n").First(l => l.StartsWith("Content-Length:"));
        var body = new byte[int.Parse(lengthLine["Content-Length:".Length..])];
        await stream.ReadExactlyAsync(body);
        return head + Encoding.ASCII.GetString(body);
    }
}
