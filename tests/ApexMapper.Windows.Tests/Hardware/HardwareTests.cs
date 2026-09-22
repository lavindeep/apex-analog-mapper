using System.Diagnostics;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Devices;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Tests.Native;
using Xunit;

namespace ApexMapper.Windows.Tests.Hardware;

/// <summary>
/// Against the maintainer's Apex Pro TKL and an interactive desktop. Run with
/// APEX_HW_TESTS=1; thresholds in <see cref="HardwareThresholds"/>. The hook and pump
/// tests inject W, which types into the foreground window on the pass-through passes.
/// </summary>
[Collection(ProcessSingletons.Name)]
public class HardwareTests
{
    private const ushort WScan = 0x11;

    private static Guid SelectedKeyboard()
    {
        var board = KeyboardDiscovery.Enumerate().FirstOrDefault(b => b.Known && b.HasVendorInterface);
        Assert.NotNull(board);
        return board.ContainerId;
    }

    private static VendorInterface OpenSelected()
    {
        var stream = HidVendorDevices.Open(SelectedKeyboard());
        Assert.NotNull(stream);
        return new VendorInterface(stream);
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
        using var device = OpenSelected();
        var reply = new byte[SensorProtocol.ReportLength];

        Assert.Equal(ExchangeStatus.Ok, device.Exchange(SensorRequest.Firmware(), reply));

        Assert.Null(SensorProtocol.ParseFirmware(reply, out var version));
        Assert.True(SensorProtocol.LooksLikeVersion(version), version);
    }

    /// <summary>Signatures come from the live board's rest replies, so every cycle is checked the way a calibrated session checks it, and the two groups must cross-reject. Hands off the keyboard.</summary>
    [HardwareFact]
    public void The_poller_cycles_two_groups_against_live_signatures_under_the_threshold_with_no_faults()
    {
        var signatures = new Dictionary<int, GroupSignature>();
        using (var device = OpenSelected())
        {
            var reply = new byte[SensorProtocol.ReportLength];
            var raw = new ushort[SensorProtocol.SensorsPerGroup];
            var filtered = new ushort[SensorProtocol.SensorsPerGroup];
            foreach (var group in new[] { 2, 3 })
            {
                Assert.Equal(ExchangeStatus.Ok, device.Exchange(SensorRequest.Group(group), reply));
                Assert.Null(SensorProtocol.ParseGroup(reply, raw, filtered));
                signatures[group] = GroupSignature.FromRest(raw);
            }
        }
        var config = PollerConfig.For([2, 3], signatures);
        Assert.True(config.SignaturesDistinguishGroups, "Groups 2 and 3 should cross-reject on this board.");
        var shared = new SensorSnapshot();
        using var poller = SensorPoller.ForKeyboard(SelectedKeyboard(), shared, config);

        poller.Start();
        Assert.True(SpinWait.SpinUntil(() => poller.State == PollerState.Running, 3000), poller.FaultReason);
        Thread.Sleep(3000);
        var p50 = poller.Stats.P50;
        var p99 = poller.Stats.P99;
        var cycles = poller.Cycles;
        var clock = Stopwatch.StartNew();
        poller.Stop();
        clock.Stop();

        Assert.Equal(0, poller.FaultCount);
        Assert.True(cycles > 150, $"{cycles} cycles in 3 s.");
        Assert.True(p99 < HardwareThresholds.SensorCycleP99Ms(2), $"p50 {p50:F2} ms, p99 {p99:F2} ms.");
        Assert.True(clock.ElapsedMilliseconds < 200, $"Stop against a live read took {clock.ElapsedMilliseconds} ms.");
        Assert.True(shared.WasRead(16) && shared.WasRead(29));
        Assert.True(SensorProtocol.LooksLikeVersion(poller.Firmware), poller.Firmware);
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

        var events = new List<RawKeyEvent>();
        try
        {
            Assert.True(Injector.SendScanCode(WScan, true));
            Assert.True(Injector.SendScanCode(WScan, false));
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
        }
        finally
        {
            Injector.SendScanCode(WScan, false);
        }
        pump.Stop();

        var w = events.Where(e => e.Code.Value == WScan).ToList();
        Assert.Equal(2, w.Count);
        Assert.True(w[0].Down);
        Assert.False(w[1].Down);
        Assert.All(w, e => Assert.Equal(0, e.Device));
        Assert.Equal(0, pump.Overflows);
        Assert.Equal(0, pump.MalformedInputs);
        Assert.True(pump.EventCount >= 2);
    }

    /// <summary>
    /// Whether the key was really swallowed is read from the asynchronous key state,
    /// which a swallowed event never reaches and a passed one does. Two passes of 200
    /// presses: swallowed (nothing typed), then passed through with the flag down (W is
    /// typed into the foreground window and the store gates the slot). Both passes are
    /// timed, so the figure covers CallNextHookEx.
    /// </summary>
    [HardwareFact]
    public void The_hook_swallows_a_synthetic_w_in_test_mode_and_the_callback_is_cheap()
    {
        var store = new KeyStateStore();
        var policy = new HookPolicy { SwallowInjected = true };
        var foreground = new ForegroundFlag { IsGameForeground = true };
        var w = new ScanCode(WScan);
        using var hook = new KeyboardHook(store, policy, foreground, [w]);

        hook.Start();
        Assert.True(hook.IsInstalled);
        var swallowedPresses = 0;
        var leakedWhileSwallowing = 0;
        var passedPresses = 0;
        var seenWhilePassing = 0;
        var gatedWhilePassing = 0;
        try
        {
            for (var i = 0; i < 200; i++)
            {
                Assert.True(Injector.SendScanCode(WScan, true));
                Assert.True(SpinWait.SpinUntil(() => store.Read(w.Slot).Digital, 500));
                Thread.Sleep(1);
                if (Injector.IsDown(Injector.VK_W))
                {
                    leakedWhileSwallowing++;
                }
                Assert.True(Injector.SendScanCode(WScan, false));
                Assert.True(SpinWait.SpinUntil(() => !store.Read(w.Slot).Digital, 500));
                swallowedPresses++;
            }
            foreground.IsGameForeground = false;
            for (var i = 0; i < 200; i++)
            {
                Assert.True(Injector.SendScanCode(WScan, true));
                Assert.True(SpinWait.SpinUntil(() => store.Read(w.Slot).Digital, 500));
                if (store.IsGated(w.Slot))
                {
                    gatedWhilePassing++;
                }
                if (SpinWait.SpinUntil(() => Injector.IsDown(Injector.VK_W), 500))
                {
                    seenWhilePassing++;
                }
                Assert.True(Injector.SendScanCode(WScan, false));
                Assert.True(SpinWait.SpinUntil(() => !store.Read(w.Slot).Digital, 500));
                passedPresses++;
            }
        }
        finally
        {
            Injector.SendScanCode(WScan, false);
        }
        var stats = hook.Snapshot();
        Assert.True(hook.Stop());

        Assert.Equal(200, swallowedPresses);
        Assert.Equal(0, leakedWhileSwallowing);
        Assert.True(policy.SwallowedCount >= 400, $"{policy.SwallowedCount} swallowed.");
        Assert.Equal(200, passedPresses);
        Assert.Equal(200, seenWhilePassing);
        Assert.Equal(200, gatedWhilePassing);
        Assert.False(store.IsGated(w.Slot));
        Assert.False(hook.IsInstalled);
        Assert.Equal(0, hook.HandlerFaults);
        Assert.True(stats.Count >= 800, $"{stats.Count} callbacks recorded.");
        Assert.True(stats.P999Ms < HardwareThresholds.HookCallbackP999Ms, $"p50 {stats.P50Ms:F3} ms, p99.9 {stats.P999Ms:F3} ms, max {stats.MaxMs:F3} ms.");
        var allowed = Math.Max(1, HardwareThresholds.HookExcursionsPerThousand * stats.Count / 1000);
        Assert.True(stats.OverOneMs <= allowed, $"{stats.OverOneMs} callbacks over 1 ms in {stats.Count}; {allowed} allowed.");
    }

    [HardwareFact]
    public void The_hook_lets_a_synthetic_w_through_when_the_game_is_not_foreground_and_raw_input_still_sees_it()
    {
        var store = new KeyStateStore();
        var policy = new HookPolicy { SwallowInjected = true };
        var foreground = new ForegroundFlag { IsGameForeground = false };
        var w = new ScanCode(WScan);
        using var hook = new KeyboardHook(store, policy, foreground, [w]);
        using var pump = new RawInputPump();

        hook.Start();
        pump.Start();
        Thread.Sleep(100);
        while (pump.TryDequeue(out _))
        {
        }
        var seen = 0;
        try
        {
            Assert.True(Injector.SendScanCode(WScan, true));
            Assert.True(Injector.SendScanCode(WScan, false));
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
        }
        finally
        {
            Injector.SendScanCode(WScan, false);
        }
        pump.Stop();
        hook.Stop();

        Assert.Equal(2, seen);
        Assert.Equal(0, policy.SwallowedCount);
        Assert.True(hook.EventCount >= 2);
        Assert.True(pump.EventCount >= 2);
    }

    /// <summary>
    /// Registry Editor always runs elevated, so it stands in for an elevated game. With it
    /// chosen and in front, this non-elevated process must report it as elevated, keep the
    /// flag down and swallow nothing. Raises a UAC prompt, so it also needs
    /// APEX_ELEVATED_CHECK=1. Close Registry Editor afterwards; this process cannot.
    /// </summary>
    [HardwareFact]
    public void An_elevated_game_in_front_is_reported_and_nothing_is_swallowed()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("APEX_ELEVATED_CHECK") == "1", "Set APEX_ELEVATED_CHECK=1 as well; it raises a UAC prompt.");
        Assert.NotEqual(true, Win32WindowSystem.Instance.IsCurrentProcessElevated());
        var regedit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
        var store = new KeyStateStore();
        var policy = new HookPolicy { SwallowInjected = true };
        var flag = new ForegroundFlag();
        using var tracker = new ForegroundTracker(flag) { GamePath = regedit };
        using var hook = new KeyboardHook(store, policy, flag, [new ScanCode(WScan)]);
        tracker.Start();
        hook.Start();

        Process.Start(new ProcessStartInfo(regedit) { UseShellExecute = true })?.Dispose();
        Assert.True(SpinWait.SpinUntil(() => tracker.Current.IsGame, 60_000), "Registry Editor never came to the front.");
        try
        {
            Injector.SendScanCode(WScan, true);
            Injector.SendScanCode(WScan, false);
        }
        finally
        {
            Injector.SendScanCode(WScan, false);
        }
        Thread.Sleep(200);
        var current = tracker.Current;
        var flagWhileInFront = flag.IsGameForeground;
        Assert.True(hook.Stop());
        Assert.True(tracker.Stop());

        Assert.Equal(Elevation.Elevated, current.Elevation);
        Assert.False(current.GameHasFocus);
        Assert.False(flagWhileInFront);
        Assert.Equal(0, policy.SwallowedCount);
        Assert.Equal(0, tracker.HandlerFaults);
        Assert.Equal(0, hook.HandlerFaults);
    }
}
