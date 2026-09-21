using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using Xunit;

namespace ApexMapper.Windows.Tests.Devices;

public class KeyboardDiscoveryTests
{
    private static readonly Guid Tkl = new("27373de1-4206-11f1-b9e4-14ac60fcc13e");
    private static readonly Guid Other = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Interfaces_fold_into_one_board_per_container_id()
    {
        var interfaces = new[]
        {
            new HidInterfaceInfo(@"\\?\hid#vid_1038&pid_1614&mi_00", 0x1614, Tkl, "SteelSeries Apex Pro TKL", false),
            new HidInterfaceInfo(@"\\?\hid#vid_1038&pid_1614&mi_01", 0x1614, Tkl, "SteelSeries Apex Pro TKL", true),
            new HidInterfaceInfo(@"\\?\hid#vid_1038&pid_1614&mi_04", 0x1614, Tkl, "SteelSeries Apex Pro TKL", false),
            new HidInterfaceInfo(@"\\?\hid#vid_1038&pid_1830&mi_00", 0x1830, Other, "SteelSeries Rival 3", false),
        };

        var boards = KeyboardDiscovery.Select(interfaces);

        Assert.Equal(2, boards.Count);
        var tkl = Assert.Single(boards, b => b.ContainerId == Tkl);
        Assert.Equal("Apex Pro TKL", tkl.Name);
        Assert.True(tkl.Known);
        Assert.True(tkl.HasVendorInterface);
        var mouse = Assert.Single(boards, b => b.ContainerId == Other);
        Assert.Equal("SteelSeries Rival 3", mouse.Name);
        Assert.False(mouse.Known);
        Assert.False(mouse.HasVendorInterface);
    }

    [Fact]
    public void An_unknown_product_without_a_name_gets_its_id()
    {
        var boards = KeyboardDiscovery.Select([new HidInterfaceInfo("p", 0x1999, Other, string.Empty, false)]);

        Assert.Equal("SteelSeries 0x1999", Assert.Single(boards).Name);
    }

    [Fact]
    public void Nothing_plugged_in_is_an_empty_list()
    {
        Assert.Empty(KeyboardDiscovery.Select([]));
    }

    [Fact]
    public void Refresh_publishes_the_current_list()
    {
        var calls = 0;
        using var discovery = new KeyboardDiscovery(() =>
        {
            calls++;
            return [new KeyboardInfo(Tkl, 0x1614, "Apex Pro TKL", true, true)];
        });
        IReadOnlyList<KeyboardInfo>? seen = null;
        discovery.Changed += list => seen = list;

        discovery.Refresh();

        Assert.Equal(1, calls);
        Assert.NotNull(seen);
        Assert.Same(seen, discovery.Current);
    }
}
