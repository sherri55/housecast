using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Ptp;
using Claunia.PropertyList;

namespace AirPlay.Engine.Receiver;

/// <summary>
/// One sender's RTSP connection to an announced receiver: pairing, the two SETUPs, timing,
/// volume and teardown, answered as shairport-sync's rtsp.c answers them (MIT).
/// </summary>
internal sealed class ReceiverSession : IAsyncDisposable
{
    private const string ServerHeader = "AirTunes/366.0";
    private const int BufferedStreamType = 103;
    private const int AudioBufferSize = 8 * 1024 * 1024;

    private readonly ReceiverIdentity _identity;
    private readonly TcpClient _connection;
    private readonly PtpFollower _clock;
    private readonly TimeSpan _lead;
    private readonly Action<string> _log;
    private readonly HomeKitPairingServer _pairing;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IDisposable> _resources = [];
    private Stream _stream;
    private double _volumeDecibels = -15;
    private BufferedAudioPlayer? _player;
    private int _disposed;

    /// <param name="lead">How long before it is due to hand on each frame of audio.</param>
    public ReceiverSession(ReceiverIdentity identity, TcpClient connection, PtpFollower clock, TimeSpan lead, Action<string> log)
    {
        _identity = identity;
        _connection = connection;
        _clock = clock;
        _lead = lead;
        _log = log;
        _pairing = new HomeKitPairingServer(identity.DeviceId, identity.PrivateKey);
        _stream = connection.GetStream();
        var remote = (IPEndPoint)connection.Client.RemoteEndPoint!;
        Sender = $"{Plain(remote.Address)}:{remote.Port}";
    }

    /// <summary>"ip:port" of the sender.</summary>
    public string Sender { get; }

    /// <summary>The name the sender gives in SETUP, e.g. "Sherri's iPhone".</summary>
    public string? SenderName { get; private set; }

    /// <summary>The sender set up an audio stream: it is playing to us, or about to.</summary>
    public event Action<ReceiverSession>? StreamStarted;

    /// <summary>The audio stream ended: torn down, or the connection closed.</summary>
    public event Action<ReceiverSession>? StreamStopped;

    /// <summary>The sender's volume in dB: 0 down to −30, or −144 for mute.</summary>
    public event Action<double>? VolumeChanged;

    public event Action<ReceivedAudio>? AudioDue;

    private IPAddress LocalAddress => Plain(((IPEndPoint)_connection.Client.LocalEndPoint!).Address);

    public async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var request = await ReadRequestAsync(_stream, _stop.Token);
                if (request is null)
                    break;
                _log($"{request.Method} {request.Uri} ({request.Body.Length} bytes)");

                var reply = await HandleAsync(request);
                await WriteResponseAsync(request, reply);

                // Pair-setup's last reply goes out in the clear; everything after it is encrypted
                if (reply.StartEncryption is { } secret)
                {
                    var (write, read) = HomeKitPairingServer.ControlChannelKeys(secret);
                    _stream = new EncryptedChannelStream(_connection.GetStream(), write, read);
                    _log("paired; the connection is now encrypted");
                }
                if (reply.Close)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or OperationCanceledException or ObjectDisposedException)
        {
            _log($"connection ended: {ex.GetType().Name}");
        }
    }

    private sealed record Reply(int Status = 200, byte[]? Body = null, string? ContentType = null)
    {
        public Dictionary<string, string> Headers { get; } = [];
        public byte[]? StartEncryption { get; init; }
        public bool Close { get; init; }
    }

    private async Task<Reply> HandleAsync(Request request)
    {
        switch (request.Method, request.Uri)
        {
            case ("GET", "/info"):
                return Plist(_identity.Info(Sender, _volumeDecibels));

            case ("POST", "/pair-setup"):
                var setupReply = _pairing.PairSetup(request.Body);
                return new Reply(Body: setupReply, ContentType: "application/octet-stream")
                {
                    StartEncryption = _pairing.SharedSecret is { } secret && _stream is NetworkStream ? secret : null,
                };

            case ("POST", "/pair-verify"):
                return new Reply(Body: _pairing.PairVerify(request.Body), ContentType: "application/octet-stream");

            case ("POST", "/fp-setup"):
                return FairPlaySetup.Reply(request.Body) is { } fairPlay
                    ? new Reply(Body: fairPlay, ContentType: "application/octet-stream")
                    : new Reply(400);

            case ("SETUP", _):
                var setup = ReadPlist(request.Body);
                // A new audio stream replaces the one playing
                if (setup.ContainsKey("streams"))
                    await StopPlayerAsync();
                return Setup(setup);

            case ("RECORD", _):
                return new Reply { Headers = { ["Audio-Latency"] = "0" } };

            case ("SETRATEANCHORTIME", _):
                SetRateAnchorTime(ReadPlist(request.Body));
                return new Reply();

            case ("FLUSHBUFFERED", _):
                var flush = ReadPlist(request.Body);
                var from = flush.ObjectForKey("flushFromSeq") is NSNumber fromNumber ? (uint?)fromNumber.ToLong() : null;
                var until = (uint)Value(flush, "flushUntilSeq");
                _log($"  flush from {from?.ToString(CultureInfo.InvariantCulture) ?? "now"} until {until}");
                _player?.Flush(from, until);
                return new Reply();

            case ("SETPEERS", _):
            case ("SETPEERSX", _):
            case ("POST", "/command"):
            case ("POST", "/audioMode"):
                return new Reply();

            case ("SET_PARAMETER", _):
                var text = Encoding.ASCII.GetString(request.Body);
                if (text.StartsWith("volume:", StringComparison.Ordinal)
                    && double.TryParse(text["volume:".Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var volume))
                {
                    _volumeDecibels = volume;
                    _log($"  volume {volume:0.0} dB");
                    VolumeChanged?.Invoke(volume);
                }
                return new Reply();

            case ("GET_PARAMETER", _):
                return new Reply(Body: Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\r\nvolume: {_volumeDecibels:0.000000}\r\n")),
                    ContentType: "text/parameters");

            case ("POST", "/feedback"):
                return new Reply();

            case ("TEARDOWN", _):
                // A teardown naming streams only stops playing; one without ends the connection
                var close = request.Body.Length == 0 || ReadPlist(request.Body).ObjectForKey("streams") is null;
                await StopPlayerAsync();
                return new Reply { Close = close, Headers = { ["Connection"] = close ? "close" : "keep-alive" } };

            case ("OPTIONS", _):
                return new Reply { Headers = { ["Public"] = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, POST, GET, PUT" } };

            default:
                _log($"  (not handled)");
                return new Reply(501);
        }
    }

    // The first SETUP describes the session and its timing; the second, with "streams", the audio
    private Reply Setup(NSDictionary request)
    {
        if (request.ObjectForKey("streams") is NSArray { Count: > 0 } streams && streams[0] is NSDictionary stream)
            return SetupStream(stream);

        SenderName = (request.ObjectForKey("name") as NSString)?.Content;
        _log($"  session from \"{SenderName}\", timing {(request.ObjectForKey("timingProtocol") as NSString)?.Content}");

        var events = Listen(out var eventPort);
        _ = AnswerEventsAsync(events);

        var address = LocalAddress.ToString();
        var reply = new NSDictionary
        {
            { "eventPort", eventPort },
            { "timingPort", 0 },
            { "timingPeerInfo", new NSDictionary
                {
                    { "Addresses", new NSArray(new NSString(address)) },
                    { "ID", address },
                }
            },
        };
        return Plist(reply);
    }

    private Reply SetupStream(NSDictionary stream)
    {
        var type = Value(stream, "type");
        var key = (stream.ObjectForKey("shk") as NSData)?.Bytes;
        _log($"  stream type {type}, ct {Value(stream, "ct")}, spf {Value(stream, "spf")}, audioFormat 0x{Value(stream, "audioFormat"):X}, shk {key?.Length ?? 0} bytes");
        if (key is not { Length: 32 })
            return new Reply(400);

        var control = UdpSockets.Bind(0);
        _resources.Add(control);
        var controlPort = ((IPEndPoint)control.Client.LocalEndPoint!).Port;

        int dataPort;
        if (type == BufferedStreamType)
        {
            var data = Listen(out dataPort);
            _player = new BufferedAudioPlayer(_clock, _lead, audio => AudioDue?.Invoke(audio), _log);
            _ = ReceiveAudioAsync(data, key, _player);
            StreamStarted?.Invoke(this);
        }
        else
        {
            var data = UdpSockets.Bind(0);
            _resources.Add(data);
            dataPort = ((IPEndPoint)data.Client.LocalEndPoint!).Port;
        }

        var reply = new NSDictionary
        {
            { "type", type },
            { "dataPort", dataPort },
            { "controlPort", controlPort },
        };
        if (type == BufferedStreamType)
            reply.Add("audioBufferSize", AudioBufferSize);
        return Plist(new NSDictionary { { "streams", new NSArray(reply) } });
    }

    // The buffered stream's TCP connection: the sender sends its packets well ahead of time
    private async Task ReceiveAudioAsync(TcpListener listener, byte[] key, BufferedAudioPlayer player)
    {
        try
        {
            using var data = await listener.AcceptTcpClientAsync(_stop.Token);
            using var audioKey = new ChaCha20Poly1305(key);
            _log("  audio connection open");
            var stream = data.GetStream();
            var packets = 0;
            var nextReport = 64;
            while (await BufferedAudioPacket.ReadAsync(stream, _stop.Token) is { } packet)
            {
                player.Add(BufferedAudioPacket.Open(audioKey, packet));
                if (++packets == nextReport)
                {
                    _log($"  audio received: {packets} packets");
                    nextReport *= 4;
                }
            }
            _log($"  audio connection closed after {packets} packets");
        }
        catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or OperationCanceledException or ObjectDisposedException)
        {
            _log($"  audio connection ended: {ex.GetType().Name} {ex.Message}");
        }
    }

    // An anchor says which RTP time plays when on the sender's clock; rate 0 pauses
    private void SetRateAnchorTime(NSDictionary request)
    {
        var rate = Value(request, "rate");
        _log($"  rate {rate}, rtpTime {Value(request, "rtpTime")}, clock {Value(request, "networkTimeTimelineID"):X16}");
        if (_player is null)
            return;
        if (request.ObjectForKey("networkTimeSecs") is NSNumber seconds)
        {
            // networkTimeFrac is a 64-bit binary fraction of a second
            var fraction = (ulong)Value(request, "networkTimeFrac");
            var nanoseconds = seconds.ToLong() * 1_000_000_000 + (long)((UInt128)fraction * 1_000_000_000 >> 64);
            _player.SetAnchor((uint)Value(request, "rtpTime"), nanoseconds, (ulong)Value(request, "networkTimeTimelineID"));
        }
        _player.SetRate(rate != 0);
    }

    private async Task StopPlayerAsync()
    {
        if (Interlocked.Exchange(ref _player, null) is { } player)
        {
            await player.DisposeAsync();
            StreamStopped?.Invoke(this);
        }
    }

    // The sender's events connection: we announce our info once, then just keep it open
    private async Task AnswerEventsAsync(TcpListener listener)
    {
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(_stop.Token);
            var secret = _pairing.SharedSecret ?? throw new IOException("Events before pairing.");
            var write = HomeKitPairing.Derive(secret, "Events-Salt", "Events-Write-Encryption-Key");
            var read = HomeKitPairing.Derive(secret, "Events-Salt", "Events-Read-Encryption-Key");
            var events = new EncryptedChannelStream(connection.GetStream(), write, read);

            var body = BinaryPropertyListWriter.WriteToArray(new NSDictionary
            {
                { "type", "updateInfo" },
                { "value", _identity.Info(Sender, _volumeDecibels) },
            });
            var head = $"POST /command RTSP/1.0\r\nCSeq: 1\r\nContent-Type: application/x-apple-binary-plist\r\nContent-Length: {body.Length}\r\n\r\n";
            await events.WriteAsync(Encoding.ASCII.GetBytes(head).Concat(body).ToArray(), _stop.Token);
            var reply = await ReadRequestAsync(events, _stop.Token);
            _log($"  events channel open; sender answered {reply?.Method}");

            // Hold the connection until the session ends
            await Task.Delay(Timeout.Infinite, _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private TcpListener Listen(out int port)
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        _resources.Add(new ListenerStopper(listener));
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private sealed class ListenerStopper(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }

    private static Reply Plist(NSDictionary body) =>
        new(Body: BinaryPropertyListWriter.WriteToArray(body), ContentType: "application/x-apple-binary-plist");

    private static NSDictionary ReadPlist(byte[] body) =>
        body.Length > 0 && PropertyListParser.Parse(body) is NSDictionary dictionary ? dictionary : new NSDictionary();

    private static long Value(NSDictionary dictionary, string key) =>
        dictionary.ObjectForKey(key) is NSNumber number ? number.ToLong() : 0;

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private async Task WriteResponseAsync(Request request, Reply reply)
    {
        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"RTSP/1.0 {reply.Status} {ReasonPhrase(reply.Status)}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"CSeq: {request.Headers.GetValueOrDefault("cseq", "0")}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Server: {ServerHeader}\r\n");
        foreach (var (name, value) in reply.Headers)
            head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        if (reply.ContentType is not null)
            head.Append(CultureInfo.InvariantCulture, $"Content-Type: {reply.ContentType}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Content-Length: {reply.Body?.Length ?? 0}\r\n\r\n");
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()).Concat(reply.Body ?? []).ToArray(), _stop.Token);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        501 => "Not Implemented",
        _ => "Error",
    };

    private sealed record Request(string Method, string Uri, Dictionary<string, string> Headers, byte[] Body);

    // A request (or reply) line, headers with lower-case names, and a body by Content-Length;
    // null when the other side closed the connection
    private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var first = await ReadLineAsync(stream, cancellationToken);
        if (first is null)
            return null;
        var headers = new Dictionary<string, string>();
        string? line;
        while ((line = await ReadLineAsync(stream, cancellationToken)) is { Length: > 0 })
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        var body = new byte[int.TryParse(headers.GetValueOrDefault("content-length"), out var length) ? length : 0];
        await stream.ReadExactlyAsync(body, cancellationToken);

        var parts = first.Split(' ');
        return new Request(parts[0], parts.Length > 1 ? parts[1] : "", headers, body);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
        }
    }

    // A session can be ended by the sender, by a newer sender and by the receiver closing
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _stop.Cancel();
        await StopPlayerAsync();
        foreach (var resource in _resources)
            resource.Dispose();
        _connection.Dispose();
        _stop.Dispose();
    }
}
