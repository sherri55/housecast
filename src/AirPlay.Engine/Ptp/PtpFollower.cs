using System.Buffers.Binary;

namespace AirPlay.Engine.Ptp;

/// <summary>
/// Follows other devices' PTP clocks without taking part, as a receiver does when an iPhone
/// plays to it: the iPhone is the clock master and sends us Sync and Follow_Up, and we only
/// listen. Each Follow_Up says when its Sync left the master; less our clock's time when the
/// Sync arrived, that is the master's offset from our clock, give or take the network delay.
/// </summary>
public sealed class PtpFollower
{
    // 2 s of samples at the iPhone's 8 Syncs a second
    private const int Window = 16;

    private readonly Dictionary<ulong, Clock> _clocks = [];

    private sealed class Clock
    {
        public ushort SyncSequence;
        public long SyncArrival;
        public readonly Queue<long> Offsets = new();
    }

    /// <summary>Takes a PTP message that arrived at <paramref name="receivedAtNanoseconds"/> on <see cref="EngineClock"/>.</summary>
    public void Handle(ReadOnlySpan<byte> message, long receivedAtNanoseconds)
    {
        if (message.Length < PtpMessages.HeaderLength)
            return;
        var type = PtpMessages.MessageType(message);
        var clockId = BinaryPrimitives.ReadUInt64BigEndian(message[20..]);
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(message[30..]);

        lock (_clocks)
        {
            if (type == PtpMessages.TypeSync)
            {
                if (!_clocks.TryGetValue(clockId, out var clock))
                    _clocks[clockId] = clock = new Clock();
                clock.SyncSequence = sequence;
                clock.SyncArrival = receivedAtNanoseconds;
            }
            else if (type == PtpMessages.TypeFollowUp && message.Length >= PtpMessages.HeaderLength + 10
                && _clocks.TryGetValue(clockId, out var clock) && clock.SyncSequence == sequence && clock.SyncArrival != 0)
            {
                clock.Offsets.Enqueue(Timestamp(message[PtpMessages.HeaderLength..]) - clock.SyncArrival);
                if (clock.Offsets.Count > Window)
                    clock.Offsets.Dequeue();
                clock.SyncArrival = 0;
            }
        }
    }

    /// <summary>
    /// The master's time less ours, in nanoseconds; false until its first Sync and Follow_Up.
    /// </summary>
    /// <param name="clockId">The master's clock identity, as a sender's anchor names its timeline.</param>
    public bool TryGetOffset(ulong clockId, out long offsetNanoseconds)
    {
        lock (_clocks)
        {
            // A delayed Sync makes the offset look smaller, never larger, so the largest
            // of the last few is the one the network delayed least
            if (_clocks.TryGetValue(clockId, out var clock) && clock.Offsets.Count > 0)
            {
                offsetNanoseconds = clock.Offsets.Max();
                return true;
            }
        }
        offsetNanoseconds = 0;
        return false;
    }

    // seconds as 48 bits, then nanoseconds as 32 bits
    private static long Timestamp(ReadOnlySpan<byte> timestamp)
    {
        var seconds = ((long)BinaryPrimitives.ReadUInt16BigEndian(timestamp) << 32) | BinaryPrimitives.ReadUInt32BigEndian(timestamp[2..]);
        return seconds * 1_000_000_000 + BinaryPrimitives.ReadUInt32BigEndian(timestamp[6..]);
    }
}
