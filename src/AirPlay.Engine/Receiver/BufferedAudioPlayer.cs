using System.Runtime.InteropServices;
using AirPlay.Engine.Audio;
using AirPlay.Engine.Ptp;

namespace AirPlay.Engine.Receiver;

/// <summary>A decoded frame of a sender's audio, and when it should be heard on <see cref="EngineClock"/>.</summary>
public sealed record ReceivedAudio(byte[] Pcm, uint RtpTime, long PlayAtNanoseconds);

/// <summary>
/// Holds a buffered stream's packets until they fall due, then decodes them and hands them
/// on in order. The sender says when to play with anchors (this RTP time plays at that moment
/// on its PTP clock, at rate 1, or pause at rate 0), and drops queued audio with flushes,
/// e.g. on a seek or a skip to the next song.
/// </summary>
internal sealed class BufferedAudioPlayer : IAsyncDisposable
{
    private const int SampleRate = 44100;
    // Audio handed on this much later than asked for is stale, e.g. what was queued before a
    // pause, or too close to its time to relay
    private const long LateLimitNanoseconds = 250_000_000;

    private readonly PtpFollower _clock;
    private readonly long _leadNanoseconds;
    private readonly Action<ReceivedAudio> _play;
    private readonly Action<string> _log;
    private readonly SortedDictionary<uint, BufferedAudioPacket> _packets = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private Anchor? _anchor;
    private bool _playing;
    private (uint From, uint Until)? _flush;
    private uint? _lastSequence;
    // AAC decoders hand each frame back one frame later, so a frame's time waits here for its audio
    private readonly Queue<(uint RtpTime, long PlayAt)> _decoding = new();
    private int _dropped;
    private bool _waitingForClock;
    private bool _reportNextPacket;

    private sealed record Anchor(uint RtpTime, long NetworkNanoseconds, ulong TimelineId);

    /// <param name="lead">How long before each frame is due to hand it on.</param>
    public BufferedAudioPlayer(PtpFollower clock, TimeSpan lead, Action<ReceivedAudio> play, Action<string> log)
    {
        _clock = clock;
        _leadNanoseconds = (long)lead.TotalMilliseconds * 1_000_000;
        _play = play;
        _log = log;
        _loop = Task.Run(() => PlayLoopAsync(_stop.Token));
    }

    public void Add(BufferedAudioPacket packet)
    {
        lock (_packets)
        {
            if (_flush is { } flush && packet.Sequence >= flush.From && packet.Sequence < flush.Until)
                return;
            if (_reportNextPacket)
                _log($"  first packet kept after the flush: {packet.Sequence}");
            _reportNextPacket = false;
            _packets[packet.Sequence] = packet;
        }
    }

    /// <summary>The audio at <paramref name="rtpTime"/> plays at <paramref name="networkNanoseconds"/> on the sender's clock.</summary>
    public void SetAnchor(uint rtpTime, long networkNanoseconds, ulong timelineId)
    {
        lock (_packets)
        {
            _anchor = new Anchor(rtpTime, networkNanoseconds, timelineId);
            if (_clock.TryGetOffset(timelineId, out var offset))
                _log($"  anchor plays in {(networkNanoseconds - offset - EngineClock.NowNanoseconds) / 1_000_000} ms; {BufferedSeconds():0.0} s of audio queued"
                    + (_packets.Count > 0 ? $", the first (seq {_packets.First().Key}) at rtpTime {_packets.First().Value.RtpTime}" : ""));
        }
    }

    public void SetRate(bool playing)
    {
        lock (_packets)
            _playing = playing;
    }

    /// <summary>Drops the packets from <paramref name="fromSequence"/> (or all before) up to <paramref name="untilSequence"/>, now and as they arrive.</summary>
    public void Flush(uint? fromSequence, uint untilSequence)
    {
        lock (_packets)
        {
            var from = fromSequence ?? 0;
            _flush = (from, untilSequence);
            _reportNextPacket = true;
            foreach (var sequence in _packets.Keys.Where(s => s >= from && s < untilSequence).ToList())
                _packets.Remove(sequence);
        }
    }

    private async Task PlayLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            // Windows' decoder belongs to the COM apartment that creates it. Made on the app's UI
            // thread, every call from this loop was marshaled to that thread, and one crashed the app.
            using var decoder = new AacDecoder();
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var (packet, playAt) in TakeDue())
                {
                    // A gap (a flush, a seek) means the decoder's history no longer applies
                    if (_lastSequence is { } last && packet.Sequence != last + 1)
                    {
                        decoder.Reset();
                        _decoding.Clear();
                    }
                    _lastSequence = packet.Sequence;
                    _decoding.Enqueue((packet.RtpTime, playAt));
                    byte[] pcm;
                    try
                    {
                        pcm = decoder.Decode(packet.Aac);
                    }
                    catch (COMException ex)
                    {
                        // Windows' decoder fails some frames around a skip; lose that frame, not the stream
                        _log($"  frame {packet.Sequence} didn't decode (0x{ex.HResult:X8}); skipped");
                        decoder.Reset();
                        _decoding.Clear();
                        continue;
                    }
                    if (pcm.Length > 0)
                    {
                        var (rtpTime, framePlayAt) = _decoding.Dequeue();
                        _play(new ReceivedAudio(pcm, rtpTime, framePlayAt));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"  playout stopped: {ex}");
        }
    }

    private List<(BufferedAudioPacket Packet, long PlayAt)> TakeDue()
    {
        var due = new List<(BufferedAudioPacket, long)>();
        lock (_packets)
        {
            if (!_playing || _anchor is not { } anchor)
                return due;
            if (!_clock.TryGetOffset(anchor.TimelineId, out var offset))
            {
                if (!_waitingForClock)
                    _log($"  waiting for the sender's clock {anchor.TimelineId:X16}");
                _waitingForClock = true;
                return due;
            }
            _waitingForClock = false;

            var now = EngineClock.NowNanoseconds;
            var dropped = _dropped;
            while (_packets.Count > 0)
            {
                var packet = _packets.First().Value;
                var playAt = anchor.NetworkNanoseconds - offset
                    + (long)unchecked((int)(packet.RtpTime - anchor.RtpTime)) * 1_000_000_000 / SampleRate;
                if (playAt - _leadNanoseconds > now)
                    break;
                _packets.Remove(packet.Sequence);
                if (now - (playAt - _leadNanoseconds) > LateLimitNanoseconds)
                    _dropped++;
                else
                    due.Add((packet, playAt));
            }
            if (_dropped != dropped)
                _log($"  dropped {_dropped - dropped} late packets");
        }
        return due;
    }

    // Each packet is one AAC frame of 1024 samples
    private double BufferedSeconds() => _packets.Count * 1024.0 / SampleRate;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop;
        _stop.Dispose();
    }
}
