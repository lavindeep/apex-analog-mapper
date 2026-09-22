using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.Windows.Tests.Session;

/// <summary>The clock is in milliseconds; checks come every 50 ms as on the hook timer.</summary>
public class WatchdogTests
{
    private sealed class Readings
    {
        public long EngineTick;
        public bool PadPresent = true;
        public long HookEvents;
        public long RawEvents;
        public bool GameFocused = true;
    }

    private static (Watchdog Watchdog, Readings Readings) Build()
    {
        var r = new Readings();
        var watchdog = new Watchdog(() => r.EngineTick, () => r.PadPresent, () => r.HookEvents, () => r.RawEvents, () => r.GameFocused, ticksPerMs: 1);
        return (watchdog, r);
    }

    [Fact]
    public void Nothing_fires_while_disarmed_as_in_starting_and_stopping()
    {
        var (watchdog, r) = Build();
        r.PadPresent = false;

        Assert.Equal(WatchdogVerdict.None, watchdog.Check(10_000));

        watchdog.Arm(10_000);
        watchdog.Disarm();
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(20_000));
    }

    [Fact]
    public void Nothing_fires_while_the_engine_ticks()
    {
        var (watchdog, r) = Build();
        watchdog.Arm(0);

        for (long now = 50; now <= 5000; now += 50)
        {
            r.EngineTick = now - 1;
            Assert.Equal(WatchdogVerdict.None, watchdog.Check(now));
        }
    }

    [Fact]
    public void A_stalled_engine_fires_once_after_two_hundred_ms()
    {
        var (watchdog, r) = Build();
        r.EngineTick = 1000;
        watchdog.Arm(1000);

        Assert.Equal(WatchdogVerdict.None, watchdog.Check(1050));
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(1100));
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(1150));
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(1200));
        Assert.Equal(WatchdogVerdict.EngineStalled, watchdog.Check(1250));
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(1300));
    }

    [Fact]
    public void A_late_check_means_the_whole_process_was_held_up_and_restarts_the_stall_clock()
    {
        var (watchdog, r) = Build();
        r.EngineTick = 1000;
        watchdog.Arm(1000);

        // A second of sleep: the hook timer itself did not run, so the engine is not blamed.
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(2000));
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(2050));
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(2200));
        Assert.Equal(WatchdogVerdict.EngineStalled, watchdog.Check(2250));
    }

    [Fact]
    public void A_lost_controller_fires_once()
    {
        var (watchdog, r) = Build();
        watchdog.Arm(0);
        r.EngineTick = 49;
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(50));

        r.PadPresent = false;
        r.EngineTick = 99;
        Assert.Equal(WatchdogVerdict.ControllerLost, watchdog.Check(100));
        r.EngineTick = 149;
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(150));
    }

    [Fact]
    public void Raw_input_counting_keys_while_the_hook_stands_still_is_a_lost_hook()
    {
        var (watchdog, r) = Build();
        watchdog.Arm(0);
        long now = 0;
        WatchdogVerdict Next(long raw, long hook)
        {
            now += 50;
            r.EngineTick = now - 1;
            r.RawEvents += raw;
            r.HookEvents += hook;
            return watchdog.Check(now);
        }

        Assert.Equal(WatchdogVerdict.None, Next(raw: 2, hook: 2));
        // One silent window can be skew between the two counters.
        Assert.Equal(WatchdogVerdict.None, Next(raw: 1, hook: 0));
        Assert.Equal(WatchdogVerdict.None, Next(raw: 1, hook: 1));
        Assert.Equal(WatchdogVerdict.None, Next(raw: 1, hook: 0));
        // No keys at all is no evidence either way.
        Assert.Equal(WatchdogVerdict.None, Next(raw: 0, hook: 0));
        Assert.Equal(WatchdogVerdict.HookLost, Next(raw: 2, hook: 0));
        Assert.Equal(WatchdogVerdict.None, Next(raw: 2, hook: 0));

        watchdog.HookReinstalled();
        Assert.Equal(WatchdogVerdict.None, Next(raw: 2, hook: 0));
        Assert.Equal(WatchdogVerdict.HookLost, Next(raw: 2, hook: 0));
    }

    [Fact]
    public void Hook_loss_is_judged_only_while_the_game_has_focus()
    {
        var (watchdog, r) = Build();
        watchdog.Arm(0);
        r.GameFocused = false;

        for (long now = 50; now <= 500; now += 50)
        {
            r.EngineTick = now - 1;
            r.RawEvents += 2;
            Assert.Equal(WatchdogVerdict.None, watchdog.Check(now));
        }
    }

    [Fact]
    public void A_reinstalled_hook_counting_from_zero_counts_as_activity()
    {
        var (watchdog, r) = Build();
        r.HookEvents = 500;
        watchdog.Arm(0);

        r.EngineTick = 49;
        r.RawEvents = 2;
        r.HookEvents = 0;
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(50));
        r.EngineTick = 99;
        r.RawEvents = 4;
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(100));
    }
}
