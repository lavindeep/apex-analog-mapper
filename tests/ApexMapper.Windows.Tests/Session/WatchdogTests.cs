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

    /// <summary>A rig that checks every 50 ms with the engine ticking, adding events to either counter before each check.</summary>
    private sealed class Clock(Watchdog watchdog, Readings readings)
    {
        private long _now;

        public WatchdogVerdict Next(long raw = 0, long hook = 0)
        {
            _now += 50;
            readings.EngineTick = _now - 1;
            readings.RawEvents += raw;
            readings.HookEvents += hook;
            return watchdog.Check(_now);
        }
    }

    private static (Clock Clock, Watchdog Watchdog, Readings Readings) Focused()
    {
        var (watchdog, r) = Build();
        watchdog.Arm(0);
        var clock = new Clock(watchdog, r);
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        return (clock, watchdog, r);
    }

    [Fact]
    public void One_key_the_hook_never_saw_is_a_lost_hook_on_the_second_check()
    {
        var (clock, _, _) = Focused();

        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1));
        Assert.Equal(WatchdogVerdict.HookLost, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1));
    }

    [Fact]
    public void Skew_in_either_direction_is_not_a_lost_hook()
    {
        var (clock, _, _) = Focused();

        // Raw Input counted first; the hook's count lands on the next check.
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1));
        Assert.Equal(WatchdogVerdict.None, clock.Next(hook: 1));
        // The hook counted first; Raw Input catches up on the next check.
        Assert.Equal(WatchdogVerdict.None, clock.Next(hook: 1));
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1));
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 3, hook: 3));
        Assert.Equal(WatchdogVerdict.None, clock.Next());
    }

    [Fact]
    public void Events_only_the_hook_sees_become_the_baseline_and_a_later_miss_still_counts()
    {
        var (clock, _, _) = Focused();

        Assert.Equal(WatchdogVerdict.None, clock.Next(hook: 2));
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1));
        Assert.Equal(WatchdogVerdict.HookLost, clock.Next());
    }

    [Fact]
    public void Hook_loss_is_judged_only_while_the_game_has_focus_and_counts_from_its_return()
    {
        var (clock, _, r) = Focused();
        r.GameFocused = false;

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 2));
        }

        r.GameFocused = true;
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 2));
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1, hook: 1));
        Assert.Equal(WatchdogVerdict.None, clock.Next());
    }

    [Fact]
    public void A_reinstalled_hook_counting_from_zero_is_rebased_and_hook_loss_can_fire_again()
    {
        var (clock, watchdog, r) = Focused();
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 5, hook: 5));
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1));
        Assert.Equal(WatchdogVerdict.HookLost, clock.Next());

        r.HookEvents = 0;
        watchdog.HookReinstalled();

        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1, hook: 1));
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: 1));
        Assert.Equal(WatchdogVerdict.HookLost, clock.Next());
    }
}