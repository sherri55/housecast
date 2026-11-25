using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Rtsp;
using Claunia.PropertyList;

namespace AirPlay.Engine;

/// <summary>
/// The RTSP side of playing to one speaker: pairing, the two SETUPs, RECORD, volume,
/// keepalive and teardown, in the order OwnTone's outputs/airplay.c uses.
/// </summary>
public sealed class AirPlaySession : IAsyncDisposable
{
    // OwnTone's stream parameters: realtime ALAC, 352 samples a packet, 44.1 kHz 16-bit stereo
    private const int StreamTypeRealtime = 96;
    private const int CompressionAlac = 2;
    private const int AudioFormatAlac44100x16x2 = 0x40000;

    private readonly AirPlaySpeaker _speaker;
    private readonly RtspClient _rtsp;
    private readonly string _clientName;
    private readonly uint _sessionId = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
    private readonly EventsChannel _events = new();
    private Timer? _keepalive;
    private bool _ending;
    private int _lost;

    public AirPlaySession(AirPlaySpeaker speaker, ulong clientId, string clientName)
    {
        _speaker = speaker;
        _clientName = clientName;
        _rtsp = new RtspClient(clientId, clientName);
    }

    public AirPlaySpeaker Speaker => _speaker;

    /// <summary>The pairing's shared secret; its first 32 bytes encrypt the audio.</summary>
    public byte[] SharedSecret { get; private set; } = [];

    /// <summary>Where audio packets go.</summary>
    public IPEndPoint DataEndPoint { get; private set; } = new(IPAddress.None, 0);

    /// <summary>Where sync packets go.</summary>
    public IPEndPoint ControlEndPoint { get; private set; } = new(IPAddress.None, 0);

    /// <summary>
    /// 0–100: the volume the speaker was at, when it was started with no volume of its own and
    /// said what its volume is.
    /// </summary>
    public int? SpeakerVolumePercent { get; private set; }

    /// <summary>
    /// Raised once if the speaker drops the session: it stopped answering, or another sender
    /// took it over. The session is no use afterwards and should be disposed.
    /// </summary>
    public event Action<AirPlaySession>? Lost;

    private string SessionUrl => $"rtsp://{_rtsp.LocalEndPoint!.Address}/{_sessionId}";

    /// <summary>
    /// Connects, pairs and sets up a realtime audio stream. Afterwards audio can be sent to
    /// <see cref="DataEndPoint"/> timed against the PTP clock.
    /// </summary>
    /// <param name="ptpClockId">The clock this sender serves, which the speaker should follow.</param>
    /// <param name="addPtpPeer">Called with the speaker's timing address once it gives it.</param>
    /// <param name="localControlPort">Our UDP port for sync packets and resend requests.</param>
    /// <param name="volumePercent">0–100, or null to leave the speaker at its own volume.</param>
    /// <param name="pairing">Keys from an earlier PIN pairing, or null for transient pairing.</param>
    /// <param name="pairingId">The identifier those keys were made with.</param>
    /// <exception cref="PairingException">The speaker refused to pair, e.g. because it wants a PIN.</exception>
    public async Task StartAsync(ulong ptpClockId, Action<IPAddress> addPtpPeer, int localControlPort, int? volumePercent,
        PairingKeys? pairing, string pairingId, CancellationToken cancellationToken = default)
    {
        await _rtsp.ConnectAsync(_speaker.Address, _speaker.Port, cancellationToken);
        await _rtsp.SendAsync("GET", "/info", cancellationToken: cancellationToken);
        SharedSecret = pairing is null
            ? await HomeKitPairing.PairTransientAsync(_rtsp, cancellationToken)
            : await HomeKitPairing.VerifyAsync(_rtsp, pairing, pairingId, cancellationToken);

        var localAddress = _rtsp.LocalEndPoint!.Address;
        var timingPeer = new NSDictionary
        {
            { "ID", Guid.NewGuid().ToString().ToUpperInvariant() },
            { "DeviceType", 0 },
            // The plist holds it as a signed 64-bit number, so its top bits make it negative
            { "ClockID", new NSNumber(unchecked((long)ptpClockId)) },
            { "SupportsClockPortMatchingOverride", false },
            { "Addresses", new NSArray(new NSString(localAddress.ToString())) },
        };
        var sessionSetup = new NSDictionary
        {
            { "name", _clientName },
            { "deviceID", FormatDeviceId(_rtsp.ClientId) },
            { "sessionUUID", Guid.NewGuid().ToString().ToUpperInvariant() },
            { "timingProtocol", "PTP" },
            { "macAddress", MacAddressOf(localAddress) },
            { "groupUUID", Guid.NewGuid().ToString().ToUpperInvariant() },
            { "groupContainsGroupLeader", false },
            { "timingPeerInfo", timingPeer },
            { "timingPeerList", new NSArray(timingPeer) },
        };
        var session = await SendPlistAsync("SETUP", sessionSetup, cancellationToken);

        var eventPort = Number(session, "eventPort");
        if (eventPort == 0)
            throw new AirPlayException($"{_speaker.Name} gave no events port.");
        // Receivers don't all need the events connection, so a failure here isn't fatal
        var eventsConnected = false;
        try
        {
            await _events.ConnectAsync(_speaker.Address, eventPort, SharedSecret, cancellationToken);
            eventsConnected = true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
        }

        // Follow the speaker's own timing address if it gives one, as OwnTone does
        var speakerTimingAddress = (session.ObjectForKey("timingPeerInfo") as NSDictionary)?.ObjectForKey("Addresses") is NSArray addresses
            ? addresses.OfType<NSString>().Select(a => IPAddress.TryParse(a.Content, out var ip) ? ip : null)
                .FirstOrDefault(ip => ip?.AddressFamily == _speaker.Address.AddressFamily)
            : null;
        addPtpPeer(speakerTimingAddress ?? _speaker.Address);

        await SendOrThrowAsync("RECORD", SessionUrl, null, null, cancellationToken);

        var peers = new NSArray(new NSString(_speaker.Address.ToString()), new NSString(localAddress.ToString()));
        // OwnTone's content type for SETPEERS, odd as it looks
        await SendOrThrowAsync("SETPEERS", SessionUrl, BinaryPropertyListWriter.WriteToArray(peers), "/peer-list-changed", cancellationToken);

        var stream = new NSDictionary
        {
            { "audioFormat", AudioFormatAlac44100x16x2 },
            { "audioMode", "default" },
            { "controlPort", localControlPort },
            { "ct", CompressionAlac },
            { "isMedia", true },
            { "latencyMax", 88200 },
            { "latencyMin", 11025 },
            { "shk", new NSData(SharedSecret[..32]) },
            { "spf", Audio.AlacFramer.SamplesPerFrame },
            { "sr", 44100 },
            { "type", StreamTypeRealtime },
            { "supportsDynamicStreamID", false },
            { "streamConnectionID", (long)_sessionId },
        };
        var streamReply = await SendPlistAsync("SETUP", new NSDictionary { ["streams"] = new NSArray(stream) }, cancellationToken);
        var streamInfo = (streamReply.ObjectForKey("streams") as NSArray)?.OfType<NSDictionary>().FirstOrDefault()
            ?? throw new AirPlayException($"{_speaker.Name} didn't describe the stream.");
        DataEndPoint = new IPEndPoint(_speaker.Address, Number(streamInfo, "dataPort"));
        ControlEndPoint = new IPEndPoint(_speaker.Address, Number(streamInfo, "controlPort"));
        if (DataEndPoint.Port == 0 || ControlEndPoint.Port == 0)
            throw new AirPlayException($"{_speaker.Name} gave no audio ports.");

        // OwnTone sends volume last: some speakers ignore it if it comes earlier. Samsung TVs
        // refuse it with 500 and play anyway, so, as in OwnTone, a refusal doesn't end the session.
        // Speakers only say their volume once the stream is set up, so it's read here too.
        try
        {
            if (volumePercent is null)
                SpeakerVolumePercent = await GetVolumeAsync(cancellationToken);
            else
                await SetVolumeAsync(volumePercent.Value, cancellationToken);
        }
        catch (AirPlayException)
        {
        }

        _keepalive = new Timer(_ => _ = KeepAliveAsync(), null, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(25));
        // The receiver closing its events channel ends the session
        if (eventsConnected)
            _ = _events.Closed.ContinueWith(_ => OnLost(), TaskScheduler.Default);
    }

    /// <summary>0–100, sent as OwnTone does: −30 dB to 0 dB in a straight line.</summary>
    public async Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default)
    {
        var decibels = -30.0 + 0.3 * Math.Clamp(percent, 0, 100);
        var body = System.Text.Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"volume: {decibels:0.000000}\r\n"));
        await SendOrThrowAsync("SET_PARAMETER", SessionUrl, body, "text/parameters", cancellationToken);
    }

    // The reply is "volume: <dB>", on the same scale SetVolumeAsync sends; −144 means muted
    private async Task<int?> GetVolumeAsync(CancellationToken cancellationToken)
    {
        var reply = await SendOrThrowAsync("GET_PARAMETER", SessionUrl, "volume\r\n"u8.ToArray(), "text/parameters", cancellationToken);
        var text = System.Text.Encoding.ASCII.GetString(reply.Body);
        var value = text[(text.IndexOf(':') + 1)..].Trim();
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decibels))
            return null;
        return (int)Math.Round(Math.Clamp((decibels + 30.0) / 0.3, 0, 100));
    }

    private async Task KeepAliveAsync()
    {
        try
        {
            if (!(await _rtsp.SendAsync("POST", "/feedback")).IsSuccess)
                OnLost();
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            OnLost();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Disposing closes the events channel too, which isn't a loss
    private void OnLost()
    {
        if (_ending || _keepalive is null || Interlocked.Exchange(ref _lost, 1) == 1)
            return;
        Lost?.Invoke(this);
    }

    private async Task<NSDictionary> SendPlistAsync(string method, NSDictionary body, CancellationToken cancellationToken)
    {
        var reply = await SendOrThrowAsync(method, SessionUrl, BinaryPropertyListWriter.WriteToArray(body), "application/x-apple-binary-plist", cancellationToken);
        return reply.Body.Length > 0 && PropertyListParser.Parse(reply.Body) is NSDictionary dictionary ? dictionary : new NSDictionary();
    }

    private async Task<RtspResponse> SendOrThrowAsync(string method, string uri, byte[]? body, string? contentType, CancellationToken cancellationToken)
    {
        var reply = await _rtsp.SendAsync(method, uri, body, contentType, cancellationToken: cancellationToken);
        if (!reply.IsSuccess)
            throw new AirPlayException($"{_speaker.Name} refused {method}: {reply.StatusCode} {reply.Reason}.");
        return reply;
    }

    private static int Number(NSDictionary dictionary, string key) =>
        dictionary.ObjectForKey(key) is NSNumber number ? number.ToInt() : 0;

    // 8 bytes as colon-separated uppercase hex, as OwnTone formats its device id
    private static string FormatDeviceId(ulong id) =>
        string.Join(':', BitConverter.GetBytes(id).Reverse().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private static string MacAddressOf(IPAddress localAddress)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(localAddress)));
        var bytes = nic?.GetPhysicalAddress().GetAddressBytes() ?? RandomNumberGenerator.GetBytes(6);
        return string.Join(':', bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    public async ValueTask DisposeAsync()
    {
        _ending = true;
        if (_keepalive is not null)
            await _keepalive.DisposeAsync();
        if (_rtsp.LocalEndPoint is not null)
        {
            try
            {
                await _rtsp.SendAsync("TEARDOWN", SessionUrl, BinaryPropertyListWriter.WriteToArray(new NSDictionary()), "application/x-apple-binary-plist");
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
            }
        }
        await _events.DisposeAsync();
        await _rtsp.DisposeAsync();
    }
}

public sealed class AirPlayException(string message) : Exception(message);
