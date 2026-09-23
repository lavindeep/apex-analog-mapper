using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

public class SensorMapTests
{
    [Fact]
    public void Default_table_maps_sixty_five_keys()
    {
        Assert.Equal(65, SensorMap.Default.SupportedCount);
    }

    [Theory]
    [InlineData(0x11, 2, 2)]
    [InlineData(0x1E, 3, 1)]
    [InlineData(0x1F, 3, 2)]
    [InlineData(0x20, 3, 3)]
    [InlineData(0x1C, 3, 13)]
    [InlineData(0x39, 5, 4)]
    public void Racing_keys_land_on_their_groups_and_slots(int scanCode, int group, int slot)
    {
        Assert.True(SensorMap.Default.TryGetSensorIndex(new ScanCode((ushort)scanCode), out var index));
        Assert.Equal(group, SensorMap.GroupOf(index));
        Assert.Equal(slot, SensorMap.SlotOf(index));
    }

    [Theory]
    [InlineData(0xE048)]
    [InlineData(0x01)]
    [InlineData(0x3B)]
    [InlineData(0x2B)]
    public void Mechanical_and_ambiguous_keys_are_unsupported(int scanCode)
    {
        Assert.False(SensorMap.Default.Supports(new ScanCode((ushort)scanCode)));
    }

    [Fact]
    public void Overrides_win_over_the_table_and_can_add_keys()
    {
        var map = new SensorMap(new Dictionary<ScanCode, int>
        {
            [new ScanCode(0x11)] = 5,
            [new ScanCode(0x2B)] = 43,
        });
        Assert.True(map.TryGetSensorIndex(new ScanCode(0x11), out var w));
        Assert.Equal(5, w);
        Assert.True(map.Supports(new ScanCode(0x2B)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SensorMap(new Dictionary<ScanCode, int> { [new ScanCode(0x11)] = 70 }));
    }
}
