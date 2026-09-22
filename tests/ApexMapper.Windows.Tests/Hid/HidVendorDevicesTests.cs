using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using Xunit;

namespace ApexMapper.Windows.Tests.Hid;

/// <summary>The interface selection rule, over candidate descriptions instead of live devices.</summary>
public class HidVendorDevicesTests
{
    private static readonly Guid Tkl = new("27373de1-4206-11f1-b9e4-14ac60fcc13e");
    private static readonly Guid Other = new("11111111-2222-3333-4444-555555555555");

    private static VendorCandidate Good(Guid? container = null) =>
        new(0x1614, container ?? Tkl, true, SensorProtocol.ReportLength, SensorProtocol.ReportLength);

    [Fact]
    public void The_one_vendor_interface_of_the_board_is_selected()
    {
        VendorCandidate[] candidates =
        [
            new(0x1614, Tkl, false, 8, 0),
            Good(),
            new(0x1614, Tkl, false, 64, 64),
        ];

        Assert.Equal(1, HidVendorDevices.Select(candidates, Tkl));
    }

    [Fact]
    public void Another_container_a_missing_usage_the_wrong_lengths_or_an_unknown_product_are_rejected()
    {
        Assert.Equal(-1, HidVendorDevices.Select([Good(Other)], Tkl));
        Assert.Equal(-1, HidVendorDevices.Select([Good() with { HasVendorUsage = false }], Tkl));
        Assert.Equal(-1, HidVendorDevices.Select([Good() with { InputLength = 64 }], Tkl));
        Assert.Equal(-1, HidVendorDevices.Select([Good() with { OutputLength = 0 }], Tkl));
        Assert.Equal(-1, HidVendorDevices.Select([Good() with { ProductId = 0x1830 }], Tkl));
        Assert.Equal(-1, HidVendorDevices.Select([], Tkl));
    }

    [Fact]
    public void Two_matches_in_one_container_select_nothing()
    {
        Assert.Equal(-1, HidVendorDevices.Select([Good(), Good()], Tkl));
    }
}
