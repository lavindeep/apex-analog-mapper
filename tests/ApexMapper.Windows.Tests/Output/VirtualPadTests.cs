using System.Diagnostics;
using ApexMapper.Core.Engine;
using ApexMapper.Windows.Output;
using Nefarius.ViGEm.Client.Exceptions;
using Xunit;

namespace ApexMapper.Windows.Tests.Output;

public class VirtualPadTests
{
    private static readonly PadReport Throttle = PadReport.Neutral with { RightTrigger = 200 };

    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private static VirtualPad Connect(FakePadDriver driver, int timeoutMs = VirtualPad.ConnectTimeoutMs) =>
        VirtualPad.Connect(() => driver, CancellationToken.None, timeoutMs);

    [Fact]
    public void Connect_nudges_then_zeros_and_returns_once_the_slot_reads_neutral()
    {
        var driver = new FakePadDriver { UserIndexUnreportedFor = 3 };

        using var pad = Connect(driver);

        Assert.Equal(0, pad.UserIndex);
        Assert.Equal(["connect", "submit LX=1 RT=0 LT=0 B=0", "submit neutral"], driver.Log.Take(3));
        Assert.Equal(PadReport.Neutral, driver.State);
    }

    [Fact]
    public void A_pad_that_never_reads_neutral_fails_to_connect_and_is_unplugged()
    {
        var driver = new FakePadDriver { ReadBackOverride = Throttle };

        var e = Assert.Throws<PadException>(() => Connect(driver, timeoutMs: 100));

        Assert.Contains("neutral", e.Message);
        Assert.False(driver.Connected);
        Assert.True(driver.Disposed);
    }

    [Fact]
    public void Cancelling_a_connect_unplugs_the_pad()
    {
        var driver = new FakePadDriver { ReadBackOverride = Throttle };
        using var cancel = new CancellationTokenSource(50);

        Assert.ThrowsAny<OperationCanceledException>(() => VirtualPad.Connect(() => driver, cancel.Token));

        Assert.False(driver.Connected);
        Assert.True(driver.Disposed);
    }

    [Fact]
    public void A_missing_driver_is_described_for_the_user()
    {
        var e = Assert.Throws<PadException>(() => VirtualPad.Connect(() => throw new VigemBusNotFoundException(), CancellationToken.None));

        Assert.Contains("ViGEmBus driver is not installed", e.Message);
    }

    [Fact]
    public void A_report_is_submitted_only_when_it_changed()
    {
        var driver = new FakePadDriver();
        using var pad = Connect(driver);
        var before = driver.Submits;

        Assert.True(pad.TrySubmit(PadReport.Neutral, Ms(100)));
        Assert.True(pad.TrySubmit(Throttle, Ms(110)));
        Assert.True(pad.TrySubmit(Throttle, Ms(120)));

        Assert.Equal(before + 1, driver.Submits);
        Assert.Equal(1, pad.SubmitCount);
        Assert.Equal(Throttle, driver.State);
    }

    [Fact]
    public void A_change_is_held_back_for_one_and_a_half_milliseconds_and_the_last_change_still_lands()
    {
        var driver = new FakePadDriver();
        using var pad = Connect(driver);
        var before = driver.Submits;
        var softer = Throttle with { RightTrigger = 100 };

        pad.TrySubmit(Throttle, Ms(100));
        pad.TrySubmit(softer, Ms(101.4));
        Assert.Equal(before + 1, driver.Submits);
        Assert.Equal(Throttle, driver.State);

        pad.TrySubmit(softer, Ms(101.6));
        Assert.Equal(before + 2, driver.Submits);
        Assert.Equal(softer, driver.State);
    }

    [Fact]
    public void After_a_claim_the_engine_can_no_longer_reach_the_driver()
    {
        var driver = new FakePadDriver();
        using var pad = Connect(driver);
        var before = driver.Submits;

        Assert.True(pad.Claim());
        Assert.False(pad.Claim());

        Assert.False(pad.TrySubmit(Throttle, Ms(100)));
        Assert.Equal(before, driver.Submits);
        Assert.True(pad.IsClaimed);
    }

    [Fact]
    public void Unplug_zeros_before_it_disconnects_and_a_second_unplug_does_nothing()
    {
        var driver = new FakePadDriver();
        var pad = Connect(driver);
        pad.TrySubmit(Throttle, Ms(100));

        Assert.True(pad.Unplug());
        Assert.True(pad.Unplug());
        pad.Dispose();

        var log = driver.Log;
        Assert.Equal(["submit neutral", "disconnect", "dispose"], log.Skip(log.Count - 3));
        Assert.Single(log, "disconnect");
        Assert.True(pad.IsUnplugged);
        Assert.Null(pad.UnplugError);
    }

    [Fact]
    public void A_submit_wedged_in_the_driver_never_touches_it_again_once_the_pad_was_taken()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var driver = new FakePadDriver();
        using var pad = Connect(driver);
        var engineThread = 0;
        driver.OnSubmit = _ =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref engineThread))
            {
                entered.Set();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        var results = new List<bool>();
        var engine = new Thread(() =>
        {
            Volatile.Write(ref engineThread, Environment.CurrentManagedThreadId);
            results.Add(pad.TrySubmit(Throttle, Ms(100)));
            results.Add(pad.TrySubmit(Throttle with { RightTrigger = 1 }, Ms(200)));
        });
        engine.Start();
        Assert.True(entered.Wait(2000, TestContext.Current.CancellationToken));

        Assert.True(pad.Unplug());
        var afterUnplug = driver.Log;
        release.Set();
        Assert.True(engine.Join(2000));

        // The call already inside the driver lands when released; nothing after it does.
        Assert.Equal([true, false], results);
        Assert.Equal(["submit neutral", "disconnect"], afterUnplug.Skip(afterUnplug.Count - 2));
        Assert.Equal([.. afterUnplug, FakePadDriver.Describe(Throttle)], driver.Log);
    }

    [Fact]
    public void A_driver_failure_on_submit_is_a_pad_exception_and_the_engine_keeps_the_pad()
    {
        var driver = new FakePadDriver();
        using var pad = Connect(driver);
        driver.OnSubmit = _ => throw new VigemTargetNotPluggedInException();

        var e = Assert.Throws<PadException>(() => pad.TrySubmit(Throttle, Ms(100)));

        Assert.Contains("unplugged by the driver", e.Message);
        Assert.False(pad.IsClaimed);
    }

    [Fact]
    public void Driver_failures_while_unplugging_are_kept_not_thrown()
    {
        var driver = new FakePadDriver();
        using var pad = Connect(driver);
        driver.OnSubmit = _ => throw new VigemTargetNotPluggedInException();

        Assert.True(pad.Unplug());

        Assert.NotNull(pad.UnplugError);
        Assert.True(pad.IsUnplugged);
        Assert.False(driver.Connected);
    }

    [Fact]
    public void Presence_follows_the_xinput_slot()
    {
        var driver = new FakePadDriver();
        using var pad = Connect(driver);

        Assert.True(pad.IsPresent());
        driver.Gone = true;
        Assert.False(pad.IsPresent());
    }

    /// <summary>A driver whose zero never returns until released, on the unplug worker only.</summary>
    private static (FakePadDriver Driver, ManualResetEventSlim Release) Hanging()
    {
        var release = new ManualResetEventSlim(false);
        var driver = new FakePadDriver
        {
            OnSubmit = _ =>
            {
                if (Thread.CurrentThread.Name == "apex-unplug")
                {
                    release.Wait(TestContext.Current.CancellationToken);
                }
            },
        };
        return (driver, release);
    }

    [Fact]
    public void A_hung_driver_holds_every_unplug_caller_for_the_bound_and_no_longer()
    {
        var (driver, release) = Hanging();
        using var _ = release;
        using var pad = Connect(driver);
        var clock = Stopwatch.StartNew();

        Assert.False(pad.Unplug());

        Assert.InRange(clock.ElapsedMilliseconds, VirtualPad.UnplugWaitMs - 50, VirtualPad.UnplugWaitMs + 1000);
        Assert.True(pad.IsClaimed);
        Assert.False(pad.IsUnplugged);
        release.Set();
        Assert.True(pad.Unplug());
        Assert.False(driver.Connected);
    }

    [Fact]
    public void A_second_caller_waits_for_the_first_unplug_and_the_pad_is_disconnected_once()
    {
        var (driver, release) = Hanging();
        using var _ = release;
        using var pad = Connect(driver);
        var results = new bool[2];
        var callers = Enumerable.Range(0, 2).Select(i => new Thread(() => results[i] = pad.Unplug())).ToArray();
        foreach (var caller in callers)
        {
            caller.Start();
        }

        Thread.Sleep(100);
        Assert.All(callers, c => Assert.True(c.IsAlive));
        release.Set();
        Assert.All(callers, c => Assert.True(c.Join(2000)));

        Assert.Equal([true, true], results);
        Assert.Single(driver.Log, "disconnect");
    }

    [Fact]
    public void Begin_unplug_claims_and_returns_at_once_while_the_worker_is_in_the_driver()
    {
        var (driver, release) = Hanging();
        using var _ = release;
        using var pad = Connect(driver);
        var clock = Stopwatch.StartNew();

        pad.BeginUnplug();

        Assert.True(clock.ElapsedMilliseconds < 100, $"BeginUnplug took {clock.ElapsedMilliseconds} ms.");
        Assert.True(pad.IsClaimed);
        Assert.False(pad.TrySubmit(Throttle, Ms(100)));
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => pad.IsUnplugged, 2000));
        Assert.False(driver.Connected);
    }

    [Fact]
    public void Unplugging_a_disposed_pad_is_a_no_op_not_a_crash()
    {
        var driver = new FakePadDriver();
        var pad = Connect(driver);
        pad.Dispose();

        pad.BeginUnplug();

        Assert.True(pad.Unplug());
        Assert.Single(driver.Log, "disconnect");
    }
}