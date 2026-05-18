using ApexMapper.Core.Diagnostics;

namespace ApexMapper.App.Diagnostics.Latency;

/// <summary>
/// Thin App-side wrapper around <see cref="HdrHistogram"/>. Keeps the
/// <c>ApexMapper.App.Diagnostics.Latency</c> namespace the natural type to
/// consume from view-models and tests, while the bucketing math itself lives
/// in <c>ApexMapper.Core</c> for cross-platform testability.
/// </summary>
public sealed class HdrHistogramAdapter
{
    private readonly HdrHistogram _histogram = new();

    /// <summary>Records a latency in microseconds.</summary>
    public void RecordMicros(long microseconds) => _histogram.Record(microseconds);

    /// <summary>Zeros all buckets and resets the running total.</summary>
    public void Reset() => _histogram.Reset();

    /// <summary>Total samples recorded since the last <see cref="Reset"/>.</summary>
    public long TotalCount => _histogram.TotalCount;

    /// <summary>Read-only view of the raw bucket counts for chart binding.</summary>
    public IReadOnlyList<long> Buckets => _histogram.Buckets;

    /// <summary>
    /// Returns P50/P95/P99 in microseconds as doubles for view-model binding.
    /// </summary>
    public (double P50, double P95, double P99) Percentiles()
    {
        var (p50, p95, p99) = _histogram.Percentiles();
        return ((double)p50, (double)p95, (double)p99);
    }
}
