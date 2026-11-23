using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AirPlay.Engine.Audio;
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Ptp;
using AirPlay.Engine.Rtp;

namespace AirPlay.Engine;

/// <summary>
/// Sends one audio stream to any number of speakers in sync. Each speaker has its own
/// session and audio key; all follow one PTP clock and one timeline, on which sample N
/// is due a fixed buffer after the moment it was handed in.
/// </summary>
public sealed class AirPlayStreamer : IAsyncDisposable
{
    public const int SampleRate = 44100;

    // OwnTone's default: speakers get each sample 2 s before it plays (start_buffer_ms 2250
    // less the receiver's own 250 ms, which latencyMin 11025 accounts for)
    private const int BufferSamples = 88200;
    private const int SyncEverySamples = SampleRate;
    private const int ResendHistory = 1000;

    private readonly PtpMaster _ptp = new();
    private readonly UdpClient _control = UdpSockets.Bind(0);
    private readonly UdpClient _data = UdpSockets.Bind(0);
    private readonly List<Target> _targets = [];
    private readonly ulong _clientId;
    private readonly string _clientName;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _resendLoop;

    private ushort _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);
    private uint _rtpTime = (uint)Random.Shared.NextInt64(uint.MaxValue);
    // The timeline: _anchorRtpTime was handed in at _anchorNanoseconds; later samples follow at 44.1 kHz
    private long _anchorNanoseconds = -1;
    private int _controlPacketsReceived;
    private int _resendRequests;

    /// <summary>For diagnosis: what speakers have sent back, and PTP sends that failed.</summary>
    public StreamerDiagnostics Diagnostics =>
        new(_controlPacketsReceived, _resendRequests, _ptp.DelayRequestsAnswered, _ptp.OtherMessages.Count, _ptp.SendFailures.Count);
    private uint _anchorRtpTime;

    public AirPlayStreamer(ulong clientId, string clientName)
    {
        _clientId = clientId;
        _clientName = clientName;
        _ptp.Start();
        _resendLoop = Task.Run(() => ResendLoopAsync(_stop.Token));
    }

    private sealed class Target(AirPlaySession session, int offsetSamples)
    {
        public AirPlaySession Session { get; } = session;
        public ChaCha20Poly1305 AudioKey { get; } = new(session.SharedSecret[..32]);
        public int OffsetSamples { get; set; } = offsetSamples;
        public Dictionary<ushort, byte[]> Sent { get; } = [];
        public bool Started { get; set; }
        public uint SamplesSinceSync { get; set; }
    }

    /// <param name="offsetMilliseconds">Plays this speaker later (positive) or earlier (negative) than the rest.</param>
    /// <param name="pairing">Keys from an earlier PIN pairing, or null for transient pairing.</param>
    /// <param name="pairingId">The identifier those keys were made with.</param>
    public async Task<AirPlaySession> AddSpeakerAsync(AirPlaySpeaker speaker, int volumePercent, int offsetMilliseconds = 0,
        PairingKeys? pairing = null, string pairingId = "", CancellationToken cancellationToken = default)
    {
        var session = new AirPlaySession(speaker, _clientId, _clientName);
        try
        {
            var controlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;
            await session.StartAsync(_ptp.ClockId, _ptp.AddPeer, controlPort, volumePercent, pairing, pairingId, cancellationToken);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }

        lock (_targets)
            _targets.Add(new Target(session, offsetMilliseconds * SampleRate / 1000));
        return session;
    }

    public async Task RemoveSpeakerAsync(AirPlaySession session)
    {
        Target? target;
        lock (_targets)
        {
            target = _targets.FirstOrDefault(t => t.Session == session);
            if (target is not null)
                _targets.Remove(target);
        }
        if (target is null)
            return;
        _ptp.RemovePeer(session.Speaker.Address);
        target.AudioKey.Dispose();
        await session.DisposeAsync();
    }

    /// <summary>
    /// Moves a speaker later (positive) or earlier (negative) than the rest while it plays.
    /// The next sync packet, sent with the next frame, tells the speaker.
    /// </summary>
    public void SetOffset(AirPlaySession session, int offsetMilliseconds)
    {
        lock (_targets)
        {
            var target = _targets.FirstOrDefault(t => t.Session == session);
            if (target is null)
                return;
            target.OffsetSamples = offsetMilliseconds * SampleRate / 1000;
            target.SamplesSinceSync = SyncEverySamples;
        }
    }

    /// <summary>
    /// Sends one frame: <see cref="AlacFramer.SamplesPerFrame"/> stereo samples, handed in
    /// at the rate they should play.
    /// </summary>
    public void Send(ReadOnlySpan<short> interleavedFrame)
    {
        var frame = AlacFramer.Encode(interleavedFrame);
        if (_anchorNanoseconds < 0)
        {
            _anchorNanoseconds = EngineClock.NowNanoseconds;
            _anchorRtpTime = _rtpTime;
        }

        Target[] targets;
        lock (_targets)
            targets = _targets.ToArray();

        foreach (var target in targets)
        {
            var starting = !target.Started;
            if (starting || target.SamplesSinceSync >= SyncEverySamples)
            {
                SendSync(target, first: starting);
                target.SamplesSinceSync = 0;
                target.Started = true;
            }

            var packet = RtpPackets.Audio(target.AudioKey, _sequence, _rtpTime, frame, marker: starting);
            _data.Send(packet, target.Session.DataEndPoint);
            lock (target.Sent)
            {
                target.Sent[_sequence] = packet;
                target.Sent.Remove((ushort)(_sequence - ResendHistory));
            }
            target.SamplesSinceSync += AlacFramer.SamplesPerFrame;
        }

        _sequence++;
        _rtpTime += AlacFramer.SamplesPerFrame;
    }

    // Says which sample plays at which clock time: the next one is due BufferSamples after it
    // was handed in, shifted by the speaker's own offset
    private void SendSync(Target target, bool first)
    {
        var handedInAt = _anchorNanoseconds + (long)(uint)(_rtpTime - _anchorRtpTime) * 1_000_000_000 / SampleRate;
        var playingNow = (uint)(_rtpTime - BufferSamples - target.OffsetSamples);
        var sync = RtpPackets.Sync(playingNow, handedInAt, _rtpTime, _ptp.ClockId, first);
        _control.Send(sync, target.Session.ControlEndPoint);
    }

    // Speakers ask on our control port for packets they missed; resend them unchanged
    private async Task ResendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _control.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            Interlocked.Increment(ref _controlPacketsReceived);
            if (RtpPackets.ResendRequest(received.Buffer) is not var (first, count))
                continue;
            Interlocked.Increment(ref _resendRequests);

            Target? target;
            lock (_targets)
                target = _targets.FirstOrDefault(t => t.Session.Speaker.Address.Equals(received.RemoteEndPoint.Address));
            if (target is null)
                continue;

            for (var i = 0; i < count; i++)
            {
                byte[]? packet;
                lock (target.Sent)
                    target.Sent.TryGetValue((ushort)(first + i), out packet);
                if (packet is not null)
                    _data.Send(packet, target.Session.DataEndPoint);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Target[] targets;
        lock (_targets)
            targets = _targets.ToArray();
        foreach (var target in targets)
            await RemoveSpeakerAsync(target.Session);

        _stop.Cancel();
        await _resendLoop;
        await _ptp.DisposeAsync();
        _control.Dispose();
        _data.Dispose();
        _stop.Dispose();
    }
}

/// <param name="ControlPackets">Packets speakers sent to our control port.</param>
/// <param name="ResendRequests">Those that asked for missed audio packets.</param>
/// <param name="DelayRequests">PTP Delay_Req answered: speakers following our clock send these.</param>
/// <param name="OtherPtpMessages">Other PTP messages speakers sent, such as their own Announce.</param>
/// <param name="PtpSendFailures">PTP messages that couldn't be sent.</param>
public readonly record struct StreamerDiagnostics(int ControlPackets, int ResendRequests, int DelayRequests, int OtherPtpMessages, int PtpSendFailures);
