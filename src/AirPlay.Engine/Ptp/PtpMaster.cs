using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AirPlay.Engine.Ptp;

/// <summary>
/// The PTP clock master every speaker in a session follows, which is what keeps several
/// speakers in sync. A port of OwnTone's libairptp (MIT): unicast to each speaker,
/// Announce and Signaling every second, two-step Sync every 125 ms, and answers to
/// each speaker's Delay_Req. Uses UDP 319 (event) and 320 (general), as receivers expect.
/// </summary>
public sealed class PtpMaster : IAsyncDisposable
{
    public const int EventPort = 319;
    public const int GeneralPort = 320;

    private readonly UdpClient _event;
    private readonly UdpClient _general;
    private readonly HashSet<IPAddress> _peers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private ushort _syncSequence;
    private ushort _announceSequence;
    private ushort _signalingSequence;

    /// <exception cref="SocketException">UDP 319 or 320 is already taken, e.g. by another AirPlay sender.</exception>
    public PtpMaster()
    {
        // Top 16 bits set, as OwnTone does; the rest random per run
        ClockId = 0xFFFF_0000_0000_0000UL | (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x0000_FFFF_FFFF_FFFFUL);
        _event = UdpSockets.Bind(EventPort);
        _general = UdpSockets.Bind(GeneralPort);
    }

    public ulong ClockId { get; }

    /// <summary>How many Delay_Req messages speakers have sent: a sign they follow this clock.</summary>
    public int DelayRequestsAnswered => _delayRequestsAnswered;

    private int _delayRequestsAnswered;
    private int _otherMessages;
    private int _lastOtherMessageType = -1;

    /// <summary>For diagnosis: other PTP messages speakers sent, and the type of the latest.</summary>
    public (int Count, int LastType) OtherMessages => (_otherMessages, _lastOtherMessageType);

    private int _sendFailures;
    private SocketError? _lastSendError;

    /// <summary>For diagnosis: sends that failed, and the latest error.</summary>
    public (int Count, SocketError? LastError) SendFailures => (_sendFailures, _lastSendError);

    public void Start()
    {
        // On the thread pool, never an app's UI thread: a Sync sent late or a Delay_Req
        // timestamped late skews every speaker's idea of our clock
        _loops.Add(Task.Run(() => SendLoopAsync(_stop.Token)));
        _loops.Add(Task.Run(() => ReceiveLoopAsync(_event, _stop.Token)));
        _loops.Add(Task.Run(() => ReceiveLoopAsync(_general, _stop.Token)));
    }

    /// <summary>
    /// Starts sending to a speaker; it hears an Announce and Signaling straight away.
    /// </summary>
    public void AddPeer(IPAddress address)
    {
        lock (_peers)
        {
            if (!_peers.Add(address))
                return;
        }
        _ = SendAsync(_general, PtpMessages.Announce(ClockId, _announceSequence++), address);
        _ = SendAsync(_general, PtpMessages.Signaling(ClockId, _signalingSequence++), address);
    }

    public void RemovePeer(IPAddress address)
    {
        lock (_peers)
            _peers.Remove(address);
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(125));
        var tick = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            IPAddress[] peers;
            lock (_peers)
                peers = _peers.ToArray();
            if (peers.Length == 0)
                continue;

            // Two-step: the Sync carries no time; the Follow_Up says when the Sync left
            var sequence = _syncSequence++;
            var sentAt = EngineClock.NowNanoseconds;
            foreach (var peer in peers)
                await SendAsync(_event, PtpMessages.Sync(ClockId, sequence), peer);
            foreach (var peer in peers)
                await SendAsync(_general, PtpMessages.FollowUp(ClockId, sequence, sentAt), peer);

            if (tick++ % 8 == 0)
            {
                var announce = PtpMessages.Announce(ClockId, _announceSequence++);
                var signaling = PtpMessages.Signaling(ClockId, _signalingSequence++);
                foreach (var peer in peers)
                {
                    await SendAsync(_general, announce, peer);
                    await SendAsync(_general, signaling, peer);
                }
            }
        }
    }

    // Speakers measure their delay to us with Delay_Req; each must be answered from port 320
    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            var receivedAt = EngineClock.NowNanoseconds;
            if (received.Buffer.Length >= PtpMessages.HeaderLength && PtpMessages.MessageType(received.Buffer) == PtpMessages.TypeDelayRequest)
            {
                await SendAsync(_general, PtpMessages.DelayResponse(ClockId, received.Buffer, receivedAt), received.RemoteEndPoint.Address);
                Interlocked.Increment(ref _delayRequestsAnswered);
            }
            else
            {
                Interlocked.Increment(ref _otherMessages);
                _lastOtherMessageType = received.Buffer.Length > 0 ? PtpMessages.MessageType(received.Buffer) : -1;
            }
        }
    }

    private async Task SendAsync(UdpClient socket, byte[] message, IPAddress peer)
    {
        try
        {
            var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            await socket.SendAsync(message, new IPEndPoint(peer, port));
        }
        catch (SocketException ex)
        {
            // A speaker that went away; the next round tries again
            Interlocked.Increment(ref _sendFailures);
            _lastSendError = ex.SocketErrorCode;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await Task.WhenAll(_loops);
        }
        catch (OperationCanceledException)
        {
        }
        _event.Dispose();
        _general.Dispose();
        _stop.Dispose();
    }
}
