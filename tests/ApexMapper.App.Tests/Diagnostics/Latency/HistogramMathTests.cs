using ApexMapper.App.Diagnostics.Latency;
using FluentAssertions;

namespace ApexMapper.App.Tests.Diagnostics.Latency;

/// <summary>
/// Smoke tests for the App-side <see cref="HdrHistogramAdapter"/>. The core
/// histogram math is exercised by ApexMapper.Core.Tests.Diagnostics.HdrHistogramTests
/// (which runs on macOS); this file verifies the adapter shell wiring on
/// Windows CI.
/// </summary>
public class HistogramMathTests
{
    [Fact]
    public void New_adapter_reports_zero_total_count()
    {
        var adapter = new HdrHistogramAdapter();
        adapter.TotalCount.Should().Be(0);
    }

    [Fact]
    public void Record_increments_total_count()
    {
        var adapter = new HdrHistogramAdapter();
        adapter.RecordMicros(100);
        adapter.RecordMicros(200);
        adapter.TotalCount.Should().Be(2);
    }

    [Fact]
    public void Percentiles_on_empty_adapter_returns_zero_tuple()
    {
        var adapter = new HdrHistogramAdapter();
        adapter.Percentiles().Should().Be((0.0, 0.0, 0.0));
    }

    [Fact]
    public void Percentiles_are_monotonic_non_decreasing()
    {
        var adapter = new HdrHistogramAdapter();
        var rng = new Random(99);
        for (var i = 0; i < 10_000; i++) adapter.RecordMicros(rng.Next(1, 10_000));

        var (p50, p95, p99) = adapter.Percentiles();
        p50.Should().BeGreaterThan(0);
        p95.Should().BeGreaterOrEqualTo(p50);
        p99.Should().BeGreaterOrEqualTo(p95);
    }

    [Fact]
    public void Reset_clears_state()
    {
        var adapter = new HdrHistogramAdapter();
        for (var i = 0; i < 500; i++) adapter.RecordMicros(i + 1);
        adapter.Reset();
        adapter.TotalCount.Should().Be(0);
        adapter.Percentiles().Should().Be((0.0, 0.0, 0.0));
    }

    [Fact]
    public void Bucket_view_is_exposed_for_chart_binding()
    {
        var adapter = new HdrHistogramAdapter();
        adapter.RecordMicros(1);
        adapter.RecordMicros(1000);
        adapter.Buckets.Should().NotBeNull();
        adapter.Buckets.Count.Should().BeGreaterThan(0);
    }
}
