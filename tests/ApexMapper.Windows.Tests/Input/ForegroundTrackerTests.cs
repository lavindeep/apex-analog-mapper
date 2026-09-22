using System.ComponentModel;
using System.Diagnostics;
using ApexMapper.Windows.Input;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

/// <summary>The tracker's evaluation, driven directly with a fake window tree; no thread, no WinEvent hook.</summary>
public class ForegroundTrackerTests
{
    private const string Forza = FakeWindows.Forza;

    [Fact]
    public void The_flag_follows_the_game_and_changed_fires_once_per_transition()
    {
        var flag = new ForegroundFlag();
        using var tracker = new ForegroundTracker(flag, FakeWindows.WithGameAndDesktop()) { GamePath = Forza };
        var events = new List<(bool Focus, bool FlagWhenRaised)>();
        tracker.Changed += info => events.Add((info.GameHasFocus, flag.IsGameForeground));

        tracker.Evaluate(100);
        tracker.Evaluate(100);
        tracker.Evaluate(300);
        tracker.Evaluate(0);
        tracker.Evaluate(100);

        Assert.True(flag.IsGameForeground);
        Assert.Equal([(true, false), (false, false), (true, false)], events);
        Assert.Equal(4242u, tracker.Current.ProcessId);
    }

    [Fact]
    public void On_gain_changed_runs_before_the_flag_flips_and_on_loss_the_flag_drops_first()
    {
        var flag = new ForegroundFlag();
        using var tracker = new ForegroundTracker(flag, FakeWindows.WithGameAndDesktop()) { GamePath = Forza };
        var flagDuringHandler = new List<bool>();
        tracker.Changed += _ => flagDuringHandler.Add(flag.IsGameForeground);

        tracker.Evaluate(100);
        tracker.Evaluate(300);

        Assert.Equal([false, false], flagDuringHandler);
    }

    [Fact]
    public void An_elevated_game_leaves_the_flag_down_and_is_reported()
    {
        var windows = FakeWindows.WithGameAndDesktop();
        windows.Elevation[4242] = true;
        var flag = new ForegroundFlag();
        using var tracker = new ForegroundTracker(flag, windows) { GamePath = Forza };
        var raised = 0;
        tracker.Changed += _ => raised++;

        tracker.Evaluate(100);

        Assert.False(flag.IsGameForeground);
        Assert.Equal(0, raised);
        Assert.True(tracker.Current.IsGame);
        Assert.Equal(Elevation.Elevated, tracker.Current.Elevation);
    }

    [Fact]
    public void Choosing_the_game_after_it_is_already_in_front_is_picked_up_on_re_evaluation()
    {
        var flag = new ForegroundFlag();
        using var tracker = new ForegroundTracker(flag, FakeWindows.WithGameAndDesktop());

        tracker.Evaluate(100);
        Assert.False(flag.IsGameForeground);
        tracker.GamePath = Forza;
        tracker.Evaluate(100);

        Assert.True(flag.IsGameForeground);
    }

    [Fact]
    public void A_throwing_changed_handler_is_counted_and_the_flag_still_moves()
    {
        var flag = new ForegroundFlag();
        using var tracker = new ForegroundTracker(flag, FakeWindows.WithGameAndDesktop()) { GamePath = Forza };
        tracker.Changed += _ => throw new InvalidOperationException("handler bug");

        tracker.Evaluate(100);
        tracker.Evaluate(300);

        Assert.Equal(2, tracker.HandlerFaults);
        Assert.False(flag.IsGameForeground);
    }
}

/// <summary>The real thread and WinEvent hook. Skips where the session has no foreground window to resolve.</summary>
[Collection(ProcessSingletons.Name)]
public class ForegroundTrackerLifecycleTests
{
    private static ForegroundTracker StartOrSkip(ForegroundFlag flag)
    {
        var tracker = new ForegroundTracker(flag);
        try
        {
            tracker.Start();
        }
        catch (Win32Exception e)
        {
            tracker.Dispose();
            Assert.Skip("This session cannot hook foreground events: " + e.Message);
        }
        if (tracker.Current.Window == 0 || tracker.Current.ImagePath is null)
        {
            tracker.Dispose();
            Assert.Skip("This session has no resolvable foreground window.");
        }
        return tracker;
    }

    [Fact]
    public void The_current_window_becomes_the_game_when_its_path_is_chosen_and_stop_clears_the_flag()
    {
        var flag = new ForegroundFlag();
        using var tracker = StartOrSkip(flag);
        var current = tracker.Current;

        tracker.GamePath = current.ImagePath;
        Assert.True(SpinWait.SpinUntil(() => tracker.Current.IsGame, 2000));
        var visible = tracker.Current.Elevation == Elevation.Visible;
        Assert.Equal(visible, flag.IsGameForeground);
        var clock = Stopwatch.StartNew();
        var stopped = tracker.Stop();
        clock.Stop();

        Assert.True(stopped);
        Assert.False(tracker.IsRunning);
        Assert.False(flag.IsGameForeground);
        Assert.Equal(0, tracker.HandlerFaults);
        Assert.True(clock.ElapsedMilliseconds < 500, $"Stop took {clock.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Stop_from_the_changed_handler_returns_at_once_and_the_thread_exits()
    {
        var flag = new ForegroundFlag();
        using var tracker = StartOrSkip(flag);
        if (tracker.Current.Elevation != Elevation.Visible)
        {
            tracker.Dispose();
            Assert.Skip("The foreground window is elevated relative to this process, so no focus transition can happen.");
        }
        var selfStop = -1;
        tracker.Changed += _ => selfStop = tracker.Stop() ? 1 : 0;

        tracker.GamePath = tracker.Current.ImagePath;

        Assert.True(SpinWait.SpinUntil(() => !tracker.IsRunning, 2000));
        Assert.Equal(0, selfStop);
        Assert.True(tracker.Stop());
        Assert.False(flag.IsGameForeground);
    }
}
