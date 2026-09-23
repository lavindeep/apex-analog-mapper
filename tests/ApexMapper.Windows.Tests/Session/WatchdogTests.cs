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
        public uint HookTime;
        public uint RawTime;
        public bool GameFocused = true;
    }

    private static (Watchdog Watchdog, Readings Readings) Build()
    {
        var r = new Readings();
        var watchdog = new Watchdog(() => r.EngineTick, () => r.PadPresent, () => r.HookTime, () => r.RawTime, () => r.GameFocused, ticksPerMs: 1);
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

    /// <summary>
    /// A rig that checks every 50 ms with the engine ticking. Each step can hand the hook
    /// and Raw Input a key event stamped with the OS time, as both see the same event.
    /// </summary>
    private sealed class Clock(Watchdog watchdog, Readings readings)
    {
        private long _now;
        private uint _osTime = 1_000_000;

        /// <summary>One check. <paramref name="raw"/> and <paramref name="hook"/> each see a new event first.</summary>
        public WatchdogVerdict Next(bool raw = false, bool hook = false)
        {
            _now += 50;
            _osTime += 50;
            readings.EngineTick = _now - 1;
            if (raw)
            {
                readings.RawTime = _osTime;
            }
            if (hook)
            {
                readings.HookTime = _osTime;
            }
            return watchdog.Check(_now);
        }

        /// <summary>A key event both see, as while the hook lives.</summary>
        public WatchdogVerdict Key() => Next(raw: true, hook: true);
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
        Assert.Equal(WatchdogVerdict.None, clock.Key());

        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
        Assert.Equal(WatchdogVerdict.HookLost, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
    }

    [Fact]
    public void Keys_both_see_and_keys_only_the_hook_sees_are_never_a_lost_hook()
    {
        var (clock, _, _) = Focused();

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(WatchdogVerdict.None, i % 3 == 0 ? clock.Next(hook: true) : clock.Key());
        }
    }

    [Fact]
    public void One_check_with_raw_input_ahead_is_skew_and_the_count_starts_over()
    {
        var (clock, _, r) = Focused();

        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
        r.HookTime = r.RawTime;
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
        r.HookTime = r.RawTime;
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
        Assert.Equal(WatchdogVerdict.HookLost, clock.Next());
    }

    [Fact]
    public void Events_from_before_the_watchdog_was_armed_never_count()
    {
        var (watchdog, r) = Build();
        r.RawTime = 5000;
        watchdog.Arm(0);
        var clock = new Clock(watchdog, r);

        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next());
    }

    [Fact]
    public void Hook_loss_is_judged_only_while_the_game_has_focus_and_counts_from_its_return()
    {
        var (clock, _, r) = Focused();
        r.GameFocused = false;

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
        }

        r.GameFocused = true;
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Key());
        Assert.Equal(WatchdogVerdict.None, clock.Next());
    }

    [Fact]
    public void A_reinstall_rebases_at_the_next_check_so_a_key_in_the_gap_is_not_held_against_the_new_hook()
    {
        var (clock, watchdog, r) = Focused();
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
        Assert.Equal(WatchdogVerdict.HookLost, clock.Next());

        watchdog.HookReinstalled();
        r.HookTime = 0;

        // A key lands after the reinstall was asked for and before the new hook is in.
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
        Assert.Equal(WatchdogVerdict.None, clock.Next());
        Assert.Equal(WatchdogVerdict.None, clock.Key());
        Assert.Equal(WatchdogVerdict.None, clock.Next(raw: true));
        Assert.Equal(WatchdogVerdict.HookLost, clock.Next());
    }

    [Fact]
    public void Event_times_that_wrap_past_zero_still_compare_in_order()
    {
        var (watchdog, r) = Build();
        r.RawTime = uint.MaxValue - 10;
        r.HookTime = uint.MaxValue - 10;
        watchdog.Arm(0);
        r.EngineTick = 49;
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(50));

        r.RawTime = 20;
        r.EngineTick = 99;
        Assert.Equal(WatchdogVerdict.None, watchdog.Check(100));
        r.EngineTick = 149;
        Assert.Equal(WatchdogVerdict.HookLost, watchdog.Check(150));
    }
}