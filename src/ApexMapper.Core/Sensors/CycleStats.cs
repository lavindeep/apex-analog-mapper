namespace ApexMapper.Core.Sensors;

/// <summary>
/// The last hundred sensor cycle periods, for the status card and for fault
/// detection. Freshness is not derived from these: a rolling percentile would grow
/// with the jitter it is meant to detect. See <see cref="SensorSnapshot.FreshnessMs"/>.
///
/// A slow cycle (100 ms or more) is not a fault on its own; the snapshot simply goes
/// stale and fallback engages. Three slow cycles in a row mean the device is not
/// answering and the handle is retired.
///
/// Written by the sensor thread, read by the UI thread. A percentile read while a
/// period is being written sees a window one sample in motion, which only blurs the
/// status number. Percentiles are read from one thread at a time.
/// </summary>
public sealed class CycleStats
{
    public const int Window = 100;
    public const float SlowCycleMs = 100f;
    public const int SlowCyclesBeforeFault = 3;

    private readonly float[] _periods = new float[Window];
    private readonly float[] _sorted = new float[Window];
    private int _count;
    private int _next;
    private int _consecutiveSlow;

    public int Count => _count;

    /// <summary>Records one period. Allocation-free. Returns true when the fault threshold is reached.</summary>
    public bool Record(float periodMs)
    {
        if (!float.IsFinite(periodMs))
        {
            return false;
        }
        _periods[_next] = periodMs;
        _next = (_next + 1) % Window;
        if (_count < Window)
        {
            _count++;
        }
        _consecutiveSlow = periodMs >= SlowCycleMs ? _consecutiveSlow + 1 : 0;
        return _consecutiveSlow >= SlowCyclesBeforeFault;
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
        _consecutiveSlow = 0;
    }

    public float P50 => Percentile(0.50f);

    public float P99 => Percentile(0.99f);

    /// <summary>Reads the count once: a Reset on the poller thread mid-call must not turn the rank negative.</summary>
    private float Percentile(float p)
    {
        var count = Volatile.Read(ref _count);
        if (count == 0)
        {
            return float.NaN;
        }
        Array.Copy(_periods, _sorted, count);
        Array.Sort(_sorted, 0, count);
        var rank = p * (count - 1);
        var lo = (int)MathF.Floor(rank);
        var hi = Math.Min(lo + 1, count - 1);
        return _sorted[lo] + (_sorted[hi] - _sorted[lo]) * (rank - lo);
    }
}
