using System.Net;
using System.Net.Sockets;
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Ptp;

namespace AirPlay.Engine.Receiver;

/// <summary>
/// An AirPlay 2 speaker this PC announces on the network, e.g. a Housecast group: iPhones,
/// iPads and Macs list it and can play to it.
/// </summary>
public sealed class AirPlayReceiver : IAsyncDisposable
{
    private readonly ReceiverIdentity _identity;
    private readonly PtpFollower _clock;
    private readonly TimeSpan _lead;
    private readonly TcpListener _listener;
    private readonly List<IDisposable> _registrations = [];
    private readonly List<ReceiverSession> _sessions = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _acceptLoop = Task.CompletedTask;
    private ReceiverSession? _streaming;

    private AirPlayReceiver(string name, KeyStore keyStore, PtpFollower clock, TimeSpan lead)
    {
        _identity = new ReceiverIdentity(name, keyStore);
        _clock = clock;
        _lead = lead;
        _listener = new TcpListener(IPAddress.Any, 0);
    }

    public string Name => _identity.Name;

    /// <summary>For diagnosis: each request senders make, and what happened.</summary>
    public event Action<string>? Logged;

    /// <summary>A sender started playing to us; its name, e.g. "Sherri's iPhone". A new sender replaces the last.</summary>
    public event Action<string>? PlaybackStarted;

    /// <summary>The sender stopped playing to us.</summary>
    public event Action? PlaybackStopped;

    /// <summary>The sender's volume in dB: 0 down to −30, or −144 for mute.</summary>
    public event Action<double>? VolumeChanged;

    /// <summary>Each decoded frame of the sender's audio, in order, the lead time before it is due.</summary>
    public event Action<ReceivedAudio>? AudioDue;

    /// <summary>Starts listening and announces the receiver under <paramref name="name"/>.</summary>
    /// <param name="clock">Follows senders' clocks: the engine's <see cref="PtpMaster.Follower"/>, as senders send to its ports.</param>
    /// <param name="lead">How long before it is due to hand on each frame; audio due sooner than that after a play or seek is skipped.</param>
    public static async Task<AirPlayReceiver> StartAsync(string name, KeyStore keyStore, PtpFollower clock, TimeSpan lead, CancellationToken cancellationToken = default)
    {
        var receiver = new AirPlayReceiver(name, keyStore, clock, lead);
        try
        {
            await receiver.StartAsync(cancellationToken);
            return receiver;
        }
        catch
        {
            await receiver.DisposeAsync();
            throw;
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptAsync(_stop.Token));

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var host = $"{Environment.MachineName}.local";
        var address = LocalAddress();
        _registrations.Add(await WindowsDnsSd.RegisterAsync($"{Name}._airplay._tcp.local", host, address, port, _identity.AirPlayTxt, cancellationToken));
        _registrations.Add(await WindowsDnsSd.RegisterAsync($"{_identity.RaopName}._raop._tcp.local", host, address, port, _identity.RaopTxt, cancellationToken));
        Log($"announced as {_identity.DeviceId} on {address}:{port}");
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            var session = new ReceiverSession(_identity, connection, _clock, _lead, message => Log($"[{connection.Client.RemoteEndPoint}] {message}"));
            session.StreamStarted += OnStreamStarted;
            session.StreamStopped += OnStreamStopped;
            session.VolumeChanged += volume => { if (_streaming == session) VolumeChanged?.Invoke(volume); };
            session.AudioDue += audio => { if (_streaming == session) AudioDue?.Invoke(audio); };
            lock (_sessions)
                _sessions.Add(session);
            _ = RunSessionAsync(session);
        }
    }

    // One sender plays at a time: a new one ends the last one's connection
    private void OnStreamStarted(ReceiverSession session)
    {
        var previous = Interlocked.Exchange(ref _streaming, session);
        if (previous is not null && previous != session)
            _ = previous.DisposeAsync().AsTask();
        PlaybackStarted?.Invoke(session.SenderName ?? session.Sender);
    }

    private void OnStreamStopped(ReceiverSession session)
    {
        if (Interlocked.CompareExchange(ref _streaming, null, session) == session)
            PlaybackStopped?.Invoke();
    }

    private async Task RunSessionAsync(ReceiverSession session)
    {
        await session.RunAsync();
        lock (_sessions)
            _sessions.Remove(session);
        await session.DisposeAsync();
    }

    private void Log(string message) => Logged?.Invoke($"{Name}: {message}");

    // The address senders reach us on: the one Windows would send internet traffic from.
    // Picking any interface with a gateway can land on a Hyper-V or WSL virtual adapter.
    private static IPAddress LocalAddress()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Connecting a UDP socket only picks a route; nothing is sent
        probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _stop.Cancel();
        _listener.Stop();
        await _acceptLoop;
        ReceiverSession[] sessions;
        lock (_sessions)
            sessions = _sessions.ToArray();
        foreach (var session in sessions)
            await session.DisposeAsync();
        _stop.Dispose();
    }
}
