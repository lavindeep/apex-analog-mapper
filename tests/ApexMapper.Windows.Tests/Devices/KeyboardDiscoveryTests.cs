using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
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
    public void A_known_product_id_labels_the_board_whichever_interface_enumerates_first()
    {
        var interfaces = new[]
        {
            new HidInterfaceInfo("a", 0x1999, Tkl, string.Empty, true),
            new HidInterfaceInfo("b", 0x1614, Tkl, "SteelSeries Apex Pro TKL", false),
        };

        var board = Assert.Single(KeyboardDiscovery.Select(interfaces));

        Assert.True(board.Known);
        Assert.Equal(0x1614, board.ProductId);
        Assert.Equal("Apex Pro TKL", board.Name);
        Assert.True(board.HasVendorInterface);
    }

    [Fact]
    public void A_throwing_subscriber_is_counted_and_is_not_an_enumeration_failure()
    {
        using var discovery = new KeyboardDiscovery(() => []);
        discovery.Changed += _ => throw new InvalidOperationException("subscriber bug");

        discovery.Refresh();
        discovery.RefreshQuietly();

        Assert.Equal(2, discovery.HandlerFaults);
        Assert.Null(discovery.LastError);
    }

    [Fact]
    public async Task Watching_debounces_a_burst_into_one_refresh_and_a_second_watch_moves_the_subscription()
    {
        var calls = 0;
        using var discovery = new KeyboardDiscovery(() =>
        {
            Interlocked.Increment(ref calls);
            return [];
        });
        var changed = 0;
        discovery.Changed += _ => Interlocked.Increment(ref changed);
        using var first = new RawInputPump();
        using var second = new RawInputPump();

        discovery.Watch(first);
        discovery.Watch(second);
        Assert.Equal(2, changed);
        second.OnDeviceChanged(1, arrived: true);
        second.OnDeviceChanged(1, arrived: false);
        second.OnDeviceChanged(2, arrived: true);
        Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => Volatile.Read(ref changed) == 3, KeyboardDiscovery.DebounceMs * 4), TestContext.Current.CancellationToken));
        first.OnDeviceChanged(3, arrived: true);
        await Task.Delay(KeyboardDiscovery.DebounceMs * 2, TestContext.Current.CancellationToken);

        Assert.Equal(3, changed);
        Assert.Equal(3, calls);
        Assert.Equal(0, first.HandlerFaults + second.HandlerFaults);
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
    public void A_device_change_after_dispose_is_ignored()
    {
        var discovery = new KeyboardDiscovery(() => []);
        discovery.Dispose();

        discovery.OnDeviceChanged(1, arrived: true);
        discovery.Dispose();
    }

    [Fact]
    public void A_failing_background_refresh_keeps_the_previous_list_reports_why_and_tries_again()
    {
        var fail = false;
        using var discovery = new KeyboardDiscovery(() => Volatile.Read(ref fail) ? throw new IOException("bus reset") : [new KeyboardInfo(Tkl, 0x1614, "Apex Pro TKL", true, true)]);
        discovery.Refresh();
        using var published = new ManualResetEventSlim();
        discovery.Changed += _ => published.Set();
        Volatile.Write(ref fail, true);

        discovery.RefreshQuietly();

        Assert.Single(discovery.Current);
        Assert.Equal("bus reset", discovery.LastError);

        // Nobody asks again: the retry after the debounce publishes, which is what resumes a paused session.
        Volatile.Write(ref fail, false);
        Assert.True(published.Wait(KeyboardDiscovery.DebounceMs * 4, TestContext.Current.CancellationToken));
        Assert.Null(discovery.LastError);
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
