namespace ApexMapper.Core.Sensors;

/// <summary>
/// One cycle's readings for every sensor, stamped at the start of the cycle. The
/// poller owns two of these and swaps which one is published, so neither thread
/// allocates. Groups the profile did not need are marked unread.
/// </summary>
public sealed class SensorSnapshot
{
    /// <summary>
    /// A snapshot older than this is stale and analog keys fall back to digital. Fixed
    /// at about five two-group cycles: a false fallback is sticky (analog resumes only
    /// after the key is read at rest), so holding a depth for 60 ms is the lesser evil.
    /// Canary exchanges and profiles with more groups both fit inside it.
    /// </summary>
    public const float FreshnessMs = 60f;

    private readonly ushort[] _raw = new ushort[SensorProtocol.SensorCount];
    private readonly ushort[] _filtered = new ushort[SensorProtocol.SensorCount];
    private readonly bool[] _groupRead = new bool[SensorRequest.GroupCount];

    /// <summary>Stopwatch ticks at the start of the cycle that produced these readings.</summary>
    public long TimestampTicks { get; private set; }

    /// <summary>Freshness limit in Stopwatch ticks, <see cref="FreshnessMs"/> converted by the poller.</summary>
    public long FreshnessLimitTicks { get; private set; }

    /// <summary>Incremented on every publish.</summary>
    public int Generation { get; private set; }

    public ReadOnlySpan<ushort> Raw => _raw;

    public ReadOnlySpan<ushort> Filtered => _filtered;

    public bool IsFresh(long nowTicks) => nowTicks - TimestampTicks < FreshnessLimitTicks;

    public bool WasRead(int sensorIndex) => _groupRead[SensorMap.GroupOf(sensorIndex) - 1];

    /// <summary>Poller side: begin filling for a new cycle.</summary>
    public void Begin(long timestampTicks, long freshnessLimitTicks, int generation)
    {
        TimestampTicks = timestampTicks;
        FreshnessLimitTicks = freshnessLimitTicks;
        Generation = generation;
        Array.Clear(_groupRead);
    }

    /// <summary>Poller side: store one parsed group.</summary>
    public void SetGroup(int group, ReadOnlySpan<ushort> raw, ReadOnlySpan<ushort> filtered)
    {
        var offset = (group - 1) * SensorProtocol.SensorsPerGroup;
        raw[..SensorProtocol.SensorsPerGroup].CopyTo(_raw.AsSpan(offset));
        filtered[..SensorProtocol.SensorsPerGroup].CopyTo(_filtered.AsSpan(offset));
        _groupRead[group - 1] = true;
    }

    public void CopyFrom(SensorSnapshot other)
    {
        other._raw.CopyTo(_raw, 0);
        other._filtered.CopyTo(_filtered, 0);
        other._groupRead.CopyTo(_groupRead, 0);
        TimestampTicks = other.TimestampTicks;
        FreshnessLimitTicks = other.FreshnessLimitTicks;
        Generation = other.Generation;
    }
}
