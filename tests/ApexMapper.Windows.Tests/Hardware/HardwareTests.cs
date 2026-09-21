using System.Diagnostics;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Native;
using Xunit;

namespace ApexMapper.Windows.Tests.Hardware;

/// <summary>
/// Against the maintainer's Apex Pro TKL and an interactive desktop. Run with
/// APEX_HW_TESTS=1; thresholds in <see cref="HardwareThresholds"/>.
/// </summary>
[Collection(ProcessSingletons.Name)]
public class HardwareTests
{
    private static Guid SelectedKeyboard()
    {
        var board = KeyboardDiscovery.Enumerate().FirstOrDefault(b => b.Known && b.HasVendorInterface);
        Assert.NotNull(board);
        return board.ContainerId;
    }

    [HardwareFact]
    public void Discovery_finds_the_connected_board_with_its_vendor_interface()
    {
        var boards = KeyboardDiscovery.Enumerate();

        var board = Assert.Single(boards, b => b.Known);
        Assert.True(board.HasVendorInterface);
        Assert.NotEqual(Guid.Empty, board.ContainerId);
    }

    [HardwareFact]
    public void The_vendor_interface_answers_the_firmware_query()
    {
        var stream = HidVendorDevices.Open(SelectedKeyboard());
        Assert.NotNull(stream);
        using var device = new VendorInterface(stream);
        var reply = new byte[SensorProtocol.ReportLength];

        Assert.Equal(ExchangeStatus.Ok, device.Exchange(SensorRequest.Firmware(), reply));

        Assert.Null(SensorProtocol.ParseFirmware(reply, out var version));
        Assert.True(SensorProtocol.LooksLikeVersion(version), version);
    }

    [HardwareFact]
    public void The_poller_cycles_two_groups_under_the_threshold_with_no_faults()
    {
        var shared = new SensorSnapshot();
        using var poller = SensorPoller.ForKeyboard(SelectedKeyboard(), shared, PollerConfig.For([2, 3]));

        poller.Start();
        Assert.True(SpinWait.SpinUntil(() => poller.State == PollerState.Running, 3000), poller.FaultReason);
        Thread.Sleep(3000);
        var p50 = poller.Stats.P50;
        var p99 = poller.Stats.P99;
        var cycles = poller.Cycles;
        poller.Stop();

        Assert.Equal(0, poller.FaultCount);
        Assert.True(cycles > 150, $"{cycles} cycles in 3 s.");
        Assert.True(p99 < HardwareThresholds.SensorCycleP99Ms(2), $"p50 {p50:F2} ms, p99 {p99:F2} ms.");
        Assert.True(shared.WasRead(16) && shared.WasRead(29));
    }

    [HardwareFact]
    public void Raw_input_sees_a_synthetic_w_with_no_device()
    {
        using var pump = new RawInputPump();
        pump.Start();
        Thread.Sleep(100);
        while (pump.TryDequeue(out _))
        {
        }

        Assert.True(User32.SendScanCode(0x11, true));
        Assert.True(User32.SendScanCode(0x11, false));
        var events = new List<RawKeyEvent>();
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                while (pump.TryDequeue(out var item))
                {
                    events.Add(item);
                }
                return events.Count >= 2;
            },
            2000));
        pump.Stop();

        var w = events.Where(e => e.Code.Value == 0x11).ToList();
        Assert.Equal(2, w.Count);
        Assert.True(w[0].Down);
        Assert.False(w[1].Down);
        Assert.All(w, e => Assert.Equal(0, e.Device));
        Assert.Equal(0, pump.Overflows);
    }

    [HardwareFact]
    public void The_hook_swallows_a_synthetic_w_in_test_mode_and_the_callback_is_cheap()
    {
        var store = new KeyStateStore();
        var policy = new HookPolicy { SwallowInjected = true };
        var foreground = new ForegroundFlag { IsGameForeground = true };
        var w = new ScanCode(0x11);
        using var hook = new KeyboardHook(store, policy, foreground, [w]);

        hook.Start();
        Assert.True(hook.IsInstalled);
        var downs = 0;
        for (var i = 0; i < 200; i++)
        {
            Assert.True(User32.SendScanCode(0x11, true));
            Assert.True(SpinWait.SpinUntil(() => store.Read(w.Slot).Digital, 500));
            downs++;
            Assert.True(User32.SendScanCode(0x11, false));
            Assert.True(SpinWait.SpinUntil(() => !store.Read(w.Slot).Digital, 500));
        }
        var stats = hook.Snapshot();
        hook.Stop();

        Assert.Equal(200, downs);
        Assert.Equal(400, policy.SwallowedCount);
        Assert.False(hook.IsInstalled);
        Assert.True(stats.Count >= 400, $"{stats.Count} callbacks recorded.");
        Assert.True(stats.P999Ms < HardwareThresholds.HookCallbackP999Ms, $"p50 {stats.P50Ms:F3} ms, p99.9 {stats.P999Ms:F3} ms, max {stats.MaxMs:F3} ms.");
        Assert.True(stats.OverOneMs * 1000 <= HardwareThresholds.HookExcursionsPerThousand * stats.Count, $"{stats.OverOneMs} callbacks over 1 ms in {stats.Count}.");
    }

    [HardwareFact]
    public void The_hook_lets_a_synthetic_w_through_when_the_game_is_not_foreground()
    {
        var store = new KeyStateStore();
        var policy = new HookPolicy { SwallowInjected = true };
        var foreground = new ForegroundFlag { IsGameForeground = false };
        var w = new ScanCode(0x11);
        using var hook = new KeyboardHook(store, policy, foreground, [w]);
        using var pump = new RawInputPump();

        hook.Start();
        pump.Start();
        Thread.Sleep(100);
        while (pump.TryDequeue(out _))
        {
        }
        Assert.True(User32.SendScanCode(0x11, true));
        Assert.True(User32.SendScanCode(0x11, false));
        var seen = 0;
        SpinWait.SpinUntil(
            () =>
            {
                while (pump.TryDequeue(out var item))
                {
                    if (item.Code == w)
                    {
                        seen++;
                    }
                }
                return seen >= 2;
            },
            2000);
        pump.Stop();
        hook.Stop();

        Assert.Equal(2, seen);
        Assert.Equal(0, policy.SwallowedCount);
    }

    [HardwareFact]
    public void The_foreground_tracker_resolves_the_current_window()
    {
        var flag = new ForegroundFlag();
        using var tracker = new ForegroundTracker(flag);

        tracker.Start();
        var current = tracker.Current;
        tracker.GamePath = current.ImagePath;
        Assert.True(SpinWait.SpinUntil(() => tracker.Current.IsGame, 2000));
        var elapsed = Stopwatch.StartNew();
        tracker.Stop();
        elapsed.Stop();

        Assert.NotEqual(0u, current.ProcessId);
        Assert.NotNull(current.ImagePath);
        Assert.False(flag.IsGameForeground);
        Assert.True(elapsed.ElapsedMilliseconds < 500);
    }
}
