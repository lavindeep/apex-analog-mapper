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
    }

    [Fact]
    public void Freshness_is_a_fixed_sixty_milliseconds()
    {
        Assert.Equal(60f, SensorSnapshot.FreshnessMs);
    }
}
