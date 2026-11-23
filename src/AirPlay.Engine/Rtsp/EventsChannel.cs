using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Engine.Pairing;

namespace AirPlay.Engine.Rtsp;

/// <summary>
/// The second, encrypted TCP connection a receiver names in its SETUP reply. Receivers send
/// remote-control requests on it; answering each with 200 OK keeps them from deciding the
/// session is dead. A minimal port of OwnTone's outputs/airplay_events.c.
/// </summary>
internal sealed class EventsChannel : IAsyncDisposable
{
    private static readonly byte[] OkReply = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nServer: AirPlay/1.0\r\n\r\n");

    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    /// <summary>Completes when the receiver closes the channel, or when this is disposed.</summary>
    public Task Closed => _loop;

    public async Task ConnectAsync(IPAddress address, int port, byte[] sharedSecret, CancellationToken cancellationToken)
    {
        await _tcp.ConnectAsync(address, port, cancellationToken);
        // The events connection is the receiver's, so the key names are the other way round
        var writeKey = Derive(sharedSecret, "Events-Read-Encryption-Key");
        var readKey = Derive(sharedSecret, "Events-Write-Encryption-Key");
        var stream = new EncryptedChannelStream(_tcp.GetStream(), writeKey, readKey);
        _loop = AnswerAsync(stream, _stop.Token);
    }

    private static async Task AnswerAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var contentLength = 0;
                string line;
                while ((line = await ReadLineAsync(stream, cancellationToken)).Length > 0)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                }
                await stream.ReadExactlyAsync(new byte[contentLength], cancellationToken);
                await stream.WriteAsync(OkReply, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or CryptographicException or ObjectDisposedException)
        {
            // The receiver closed the channel or the session is ending
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
                throw new IOException("Events channel closed.");
            if (one[0] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] != '\r')
                bytes.Add(one[0]);
        }
    }

    private static byte[] Derive(byte[] secret, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32, "Events-Salt"u8.ToArray(), Encoding.ASCII.GetBytes(info));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _tcp.Dispose();
        await _loop;
        _stop.Dispose();
    }
}
