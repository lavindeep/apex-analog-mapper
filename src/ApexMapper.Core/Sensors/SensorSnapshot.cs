using System.Diagnostics;

namespace ApexMapper.Core.Sensors;

/// <summary>
/// One cycle's readings for every sensor, stamped when the cycle's last group lands. The
/// poller fills a private working snapshot with <see cref="Begin"/> and
/// <see cref="SetGroup"/>, then copies it into the shared one with
/// <see cref="Publish"/>. The copy runs under a sequence lock: the generation is odd
/// while the copy is in progress, so a reader that sees an odd generation, or a
/// generation that changed while it read, knows the data is torn and retries. Neither
/// side allocates.
/// </summary>
public sealed class SensorSnapshot
{
    /// <summary>
    /// A snapshot older than this is stale and analog keys fall back to digital. Fixed
    /// at about five two-group cycles. A rolling percentile would grow with the jitter
    /// it is meant to catch, and the handover back to analog is a short ramp, so a
    /// false fallback costs little. Canary exchanges and profiles with more groups fit inside it.
    /// </summary>
    public const float FreshnessMs = 60f;

    /// <summary>A reader that tears this many times in a row treats the snapshot as unavailable.</summary>
    public const int MaxReadAttempts = 3;

    private readonly ushort[] _raw = new ushort[SensorProtocol.SensorCount];
    private readonly ushort[] _filtered = new ushort[SensorProtocol.SensorCount];
    private readonly bool[] _groupRead = new bool[SensorRequest.GroupCount];
    private readonly long _freshnessLimitTicks;
    private int _generation;

    /// <summary>Ticks are Stopwatch ticks.</summary>
    public SensorSnapshot() : this(Stopwatch.Frequency / 1000d)
    {
    }

    /// <summary>Ticks in whatever unit the caller's clock uses; tests pass 1 for milliseconds.</summary>
    public SensorSnapshot(double ticksPerMs)
    {
        if (!double.IsFinite(ticksPerMs) || ticksPerMs <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerMs), ticksPerMs, "Ticks per millisecond must be positive.");
        }
        _freshnessLimitTicks = (long)Math.Round(FreshnessMs * ticksPerMs);
    }

    /// <summary>Clock ticks at the start of the cycle that produced these readings.</summary>
    public long TimestampTicks { get; private set; }

    /// <summary><see cref="FreshnessMs"/> in the clock's ticks.</summary>
    public long FreshnessLimitTicks => _freshnessLimitTicks;

    /// <summary>Even when stable, odd while a publish is copying. Two more per publish.</summary>
    public int Generation => Volatile.Read(ref _generation);

    public ReadOnlySpan<ushort> Raw => _raw;

    public ReadOnlySpan<ushort> Filtered => _filtered;

    public bool IsFresh(long nowTicks) => nowTicks - TimestampTicks < _freshnessLimitTicks;

    public bool WasRead(int sensorIndex) => _groupRead[SensorMap.GroupOf(sensorIndex) - 1];

    /// <summary>Poller side, on the working snapshot: start a new cycle. Every group is unread.</summary>
    public void Begin(long timestampTicks)
    {
        TimestampTicks = timestampTicks;
        Array.Clear(_groupRead);
    }

    /// <summary>
    /// Poller side, on the working snapshot: the time the last group landed. Freshness
    /// counts from here, so a snapshot's age at the engine is at most one cycle plus a
    /// canary rather than two cycles, which is what lets five groups (30 ms) fit the
    /// 60 ms limit.
    /// </summary>
    public void Stamp(long timestampTicks) => TimestampTicks = timestampTicks;

    /// <summary>Poller side, on the working snapshot: store one parsed group.</summary>
    public void SetGroup(int group, ReadOnlySpan<ushort> raw, ReadOnlySpan<ushort> filtered)
    {
        if (group is < 1 or > SensorRequest.GroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(group), group, "Sensor group must be 1..5.");
        }
        var offset = (group - 1) * SensorProtocol.SensorsPerGroup;
        raw[..SensorProtocol.SensorsPerGroup].CopyTo(_raw.AsSpan(offset));
        filtered[..SensorProtocol.SensorsPerGroup].CopyTo(_filtered.AsSpan(offset));
        _groupRead[group - 1] = true;
    }

    /// <summary>
    /// Poller side, on the shared snapshot: copy the working snapshot in. Only one
    /// thread may publish. The full fence on the odd write keeps the data writes after
    /// it; the release on the even write keeps them before it.
    /// </summary>
    public void Publish(SensorSnapshot working)
    {
        var generation = _generation;
        Interlocked.Exchange(ref _generation, generation + 1);
        working._raw.CopyTo(_raw, 0);
        working._filtered.CopyTo(_filtered, 0);
        working._groupRead.CopyTo(_groupRead, 0);
        TimestampTicks = working.TimestampTicks;
        Volatile.Write(ref _generation, generation + 2);
    }

    /// <summary>Reader side: false while a publish is in progress.</summary>
    public bool TryBeginRead(out int generation)
    {
        generation = Volatile.Read(ref _generation);
        return (generation & 1) == 0;
    }

    /// <summary>Reader side: true when nothing was published since <see cref="TryBeginRead"/>.</summary>
    public bool EndRead(int generation)
    {
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _generation) == generation;
    }
}
