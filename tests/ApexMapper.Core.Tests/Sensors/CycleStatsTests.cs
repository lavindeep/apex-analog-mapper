using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

public class CycleStatsTests
{
    [Fact]
    public void Percentiles_over_the_measured_cycle_shape()
    {
        var stats = new CycleStats();
        for (var i = 0; i < 99; i++)
        {
            stats.Record(12f);
        }
        stats.Record(24f);
        Assert.Equal(12f, stats.P50);
        Assert.InRange(stats.P99, 12f, 24f);
    }

    [Fact]
    public void Window_keeps_only_the_last_hundred()
    {
        var stats = new CycleStats();
        for (var i = 0; i < 100; i++)
        {
            stats.Record(50f);
        }
        for (var i = 0; i < 100; i++)
        {
            stats.Record(12f);
        }
        Assert.Equal(12f, stats.P99);
        Assert.Equal(100, stats.Count);
        Assert.True(float.IsNaN(new CycleStats().P50));
    }

    [Fact]
    public void One_slow_cycle_is_not_a_fault_but_three_in_a_row_are()
    {
        var stats = new CycleStats();
        Assert.False(stats.Record(140f));
        Assert.False(stats.Record(12f));
        Assert.False(stats.Record(140f));
        Assert.False(stats.Record(140f));
        Assert.True(stats.Record(140f));
        stats.Reset();
        Assert.False(stats.Record(140f));
        Assert.Equal(1, stats.Count);
        Assert.Equal(140f, stats.P50);
        Assert.Equal(140f, stats.P99);
    }

    [Fact]
    public void Partial_window_interpolates_between_samples()
    {
        var stats = new CycleStats();
        stats.Record(30f);
        stats.Record(10f);
        stats.Record(20f);
        Assert.Equal(20f, stats.P50);
        Assert.Equal(29.8f, stats.P99, 0.001f);
    }

    [Fact]
    public void A_non_finite_period_is_ignored()
    {
        var stats = new CycleStats();
        stats.Record(140f);
        stats.Record(140f);
        Assert.False(stats.Record(float.NaN));
        Assert.Equal(2, stats.Count);
        Assert.Equal(140f, stats.P50);
        Assert.True(stats.Record(140f));
    }
}
