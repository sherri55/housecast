using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirPlay.Engine.Rtsp;

/// <summary>
/// One RTSP connection to an AirPlay receiver. Requests and replies are plain text headers
/// with binary bodies. After pairing, <see cref="UseStream"/> swaps in an encrypting stream.
/// </summary>
public sealed class RtspClient : IAsyncDisposable
{
    // The values OwnTone sends (outputs/airplay.c request_headers_add, conffile.c)
    private const string UserAgent = "AirPlay/999.0.0";

    private readonly TcpClient _tcp = new() { NoDelay = true };
    // One request at a time: a keepalive timer and a volume change must not interleave on the wire
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private Stream _stream = Stream.Null;
    private int _cseq = 1;

    /// <param name="clientId">Identifies this sender to the receiver, sent as Client-Instance and DACP-ID.</param>
    /// <param name="clientName">Shown by some receivers as the source's name.</param>
    public RtspClient(ulong clientId, string clientName)
    {
        ClientId = clientId;
        ClientName = clientName;
    }

    public ulong ClientId { get; }
    public string ClientName { get; }

    /// <summary>
    /// Echoed back by receivers when they send remote-control commands; OwnTone found some
    /// receivers only cope with a 32-bit value.
    /// </summary>
    public uint ActiveRemote { get; } = (uint)Random.Shared.Next();

    // TcpClient's socket is dual-mode, so an IPv4 connection reports ::ffff:a.b.c.d; receivers
    // are given this address for PTP and the session URL and only understand the plain IPv4 form
    public IPEndPoint? LocalEndPoint => _tcp.Client.LocalEndPoint is IPEndPoint local
        ? new IPEndPoint(local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address, local.Port)
        : null;

    public async Task ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        await _tcp.ConnectAsync(address, port, cancellationToken);
        _stream = _tcp.GetStream();
    }

    /// <summary>
    /// Replaces the stream requests and replies go through, e.g. with an encrypting one after pairing.
    /// </summary>
    public void UseStream(Func<Stream, Stream> wrap) => _stream = wrap(_stream);

    public async Task<RtspResponse> SendAsync(
        string method,
        string uri,
        byte[]? body = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            return await SendOneAsync(method, uri, body, contentType, headers, cancellationToken);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<RtspResponse> SendOneAsync(
        string method,
        string uri,
        byte[]? body,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var request = new StringBuilder();
        request.Append($"{method} {uri} RTSP/1.0\r\n");
        request.Append($"CSeq: {_cseq++}\r\n");
        request.Append($"User-Agent: {UserAgent}\r\n");
        request.Append($"X-Apple-Client-Name: {ClientName}\r\n");
        request.Append($"Client-Instance: {ClientId:X16}\r\n");
        request.Append($"DACP-ID: {ClientId:X16}\r\n");
        request.Append($"Active-Remote: {ActiveRemote}\r\n");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Append($"{name}: {value}\r\n");
        if (contentType is not null)
            request.Append($"Content-Type: {contentType}\r\n");
        request.Append($"Content-Length: {body?.Length ?? 0}\r\n\r\n");

        var head = Encoding.ASCII.GetBytes(request.ToString());
        var message = new byte[head.Length + (body?.Length ?? 0)];
        head.CopyTo(message, 0);
        body?.CopyTo(message, head.Length);

        await _stream.WriteAsync(message, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
        return await ReadResponseAsync(cancellationToken);
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var statusLine = await ReadLineAsync(cancellationToken);
        // "RTSP/1.0 200 OK"
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var statusCode))
            throw new InvalidDataException($"Not an RTSP reply: '{statusLine}'");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string line;
        while ((line = await ReadLineAsync(cancellationToken)).Length > 0)
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var length = headers.TryGetValue("Content-Length", out var text)
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : 0;
        var body = new byte[length];
        await _stream.ReadExactlyAsync(body, cancellationToken);

        return new RtspResponse(statusCode, parts.Length > 2 ? parts[2] : "", headers, body);
    }

    // Byte at a time, because the body after the headers is binary and must stay in the stream
    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await _stream.ReadAsync(one, cancellationToken) == 0)
                throw new EndOfStreamException("The receiver closed the RTSP connection.");
            if (one[0] == '\n')
                break;
            if (one[0] != '\r')
                bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
