using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

public class SensorSnapshotTests
{
    [Fact]
    public void Freshness_is_age_against_the_limit()
    {
        var snapshot = new SensorSnapshot();
        snapshot.Begin(timestampTicks: 1000, freshnessLimitTicks: 100, generation: 1);
        Assert.True(snapshot.IsFresh(1099));
        Assert.False(snapshot.IsFresh(1100));
    }

    [Fact]
    public void Groups_land_at_their_flat_offsets_and_unread_groups_are_reported()
    {
        var snapshot = new SensorSnapshot();
        snapshot.Begin(0, 1, 1);
        var raw = new ushort[14];
        var filtered = new ushort[14];
        raw[2] = 4095;
        snapshot.SetGroup(2, raw, filtered);
        Assert.Equal(4095, snapshot.Raw[14 + 2]);
        Assert.True(snapshot.WasRead(16));
        Assert.False(snapshot.WasRead(30));
    }

    [Fact]
    public void Copy_carries_everything()
    {
        var source = new SensorSnapshot();
        source.Begin(5, 6, 7);
        var raw = new ushort[14];
        raw[0] = 123;
        source.SetGroup(1, raw, new ushort[14]);
        var copy = new SensorSnapshot();
        copy.CopyFrom(source);
        Assert.Equal(5, copy.TimestampTicks);
        Assert.Equal(6, copy.FreshnessLimitTicks);
        Assert.Equal(7, copy.Generation);
        Assert.Equal(123, copy.Raw[0]);
        Assert.True(copy.WasRead(0));
    }

    [Fact]
    public void Known_keyboards_table_marks_only_gen_1_as_verified()
    {
        Assert.True(KnownKeyboards.IsVerified(0x1614, "4.9.1"));
        Assert.True(KnownKeyboards.IsVerified(0x1610, "4.9.1"));
        Assert.False(KnownKeyboards.IsVerified(0x1614, "5.0.0"));
        Assert.False(KnownKeyboards.IsVerified(0x1642, "4.9.1"));
        Assert.True(KnownKeyboards.IsApexPro(0x1642));
        Assert.False(KnownKeyboards.IsApexPro(0x1612));
    }
}
