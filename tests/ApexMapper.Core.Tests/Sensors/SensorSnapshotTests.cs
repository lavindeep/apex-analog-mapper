using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

public class SensorSnapshotTests
{
    [Fact]
    public void Freshness_is_sixty_milliseconds_in_the_clock_s_ticks()
    {
        var snapshot = new SensorSnapshot(ticksPerMs: 1);
        snapshot.Begin(1000);
        Assert.Equal(60, snapshot.FreshnessLimitTicks);
        Assert.True(snapshot.IsFresh(1059));
        Assert.False(snapshot.IsFresh(1060));

        var tenPerMs = new SensorSnapshot(ticksPerMs: 10);
        Assert.Equal(600, tenPerMs.FreshnessLimitTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SensorSnapshot(0));
    }

    [Fact]
    public void Groups_land_at_their_flat_offsets_and_begin_marks_every_group_unread()
    {
        var snapshot = new SensorSnapshot(1);
        snapshot.Begin(0);
        var raw = new ushort[14];
        var filtered = new ushort[14];
        raw[2] = 4095;
        snapshot.SetGroup(2, raw, filtered);
        Assert.Equal(4095, snapshot.Raw[14 + 2]);
        Assert.True(snapshot.WasRead(16));
        Assert.False(snapshot.WasRead(30));

        snapshot.Begin(5);
        Assert.False(snapshot.WasRead(16));
        Assert.Equal(4095, snapshot.Raw[16]);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.SetGroup(6, raw, filtered));
    }

    [Fact]
    public void Publish_copies_the_working_snapshot_and_steps_the_generation_by_two()
    {
        var working = new SensorSnapshot(1);
        working.Begin(5);
        var raw = new ushort[14];
        raw[0] = 123;
        working.SetGroup(1, raw, new ushort[14]);

        var shared = new SensorSnapshot(1);
        Assert.Equal(0, shared.Generation);
        shared.Publish(working);
        Assert.Equal(2, shared.Generation);
        Assert.Equal(5, shared.TimestampTicks);
        Assert.Equal(123, shared.Raw[0]);
        Assert.True(shared.WasRead(0));
        Assert.False(shared.WasRead(14));

        // The copy is independent of the working buffer.
        raw[0] = 999;
        working.SetGroup(1, raw, new ushort[14]);
        working.Begin(9);
        Assert.Equal(123, shared.Raw[0]);
        Assert.Equal(5, shared.TimestampTicks);
    }

    [Fact]
    public void A_read_that_spans_a_publish_is_reported_torn()
    {
        var shared = new SensorSnapshot(1);
        Assert.True(shared.TryBeginRead(out var generation));
        Assert.True(shared.EndRead(generation));

        var working = new SensorSnapshot(1);
        working.Begin(1);
        Assert.True(shared.TryBeginRead(out generation));
        shared.Publish(working);
        Assert.False(shared.EndRead(generation));
        Assert.True(shared.TryBeginRead(out generation));
        Assert.True(shared.EndRead(generation));
    }

    [Fact]
    public void Known_keyboards_table_marks_only_the_measured_tkl_as_verified()
    {
        Assert.True(KnownKeyboards.IsVerified(0x1614, "4.9.1"));
        Assert.False(KnownKeyboards.IsVerified(0x1610, "4.9.1"));
        Assert.True(KnownKeyboards.IsApexPro(0x1610));
        Assert.True(KnownKeyboards.IsVerified(0x1614, "4.16.8"));
        Assert.False(KnownKeyboards.IsVerified(0x1614, "5.0.0"));
        Assert.False(KnownKeyboards.IsVerified(0x1642, "4.9.1"));
        Assert.True(KnownKeyboards.IsApexPro(0x1642));
        Assert.False(KnownKeyboards.IsApexPro(0x1612));
    }
}
