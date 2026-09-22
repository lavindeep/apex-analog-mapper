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
    public void Changes_are_capped_at_one_submit_per_two_milliseconds_and_the_last_change_still_lands()
    {
        var driver = new FakePadDriver();
        using var pad = Connect(driver);
        var before = driver.Submits;
        var softer = Throttle with { RightTrigger = 100 };

        pad.TrySubmit(Throttle, Ms(100));
        pad.TrySubmit(softer, Ms(101));
        Assert.Equal(before + 1, driver.Submits);
        Assert.Equal(Throttle, driver.State);

        pad.TrySubmit(softer, Ms(102));
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
}
