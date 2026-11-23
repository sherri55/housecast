using System.Diagnostics;

namespace AirPlay.Engine.Ptp;

/// <summary>
/// The one clock the engine keeps time with: PTP messages and the audio's sync packets
/// must agree on it, as OwnTone's both use CLOCK_MONOTONIC. Built on the high-resolution
/// performance counter, so it never jumps when Windows adjusts the wall clock.
/// </summary>
public static class EngineClock
{
    public static long NowNanoseconds => ToNanoseconds(Stopwatch.GetTimestamp());

    public static long ToNanoseconds(long stopwatchTicks) =>
        (long)((Int128)stopwatchTicks * 1_000_000_000 / Stopwatch.Frequency);
}
