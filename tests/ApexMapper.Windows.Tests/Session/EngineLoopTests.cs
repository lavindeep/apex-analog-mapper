using System.Diagnostics;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Output;
using ApexMapper.Windows.Session;
using ApexMapper.Windows.Tests.Output;
using Nefarius.ViGEm.Client.Exceptions;
using Xunit;
using static ApexMapper.Windows.Tests.Session.SessionFixtures;

namespace ApexMapper.Windows.Tests.Session;

public class EngineLoopTests
{
    private static readonly long OneMs = Stopwatch.Frequency / 1000;

    private sealed record Rig(EngineLoop Engine, FakePadDriver Driver, VirtualPad Pad, KeyStateStore Store, ForegroundFlag Flag);

    private static Rig Build(bool withSnapshot = true, params (int Index, ushort Raw)[] overrides)
    {
        var driver = new FakePadDriver();
        var pad = VirtualPad.Connect(() => driver, CancellationToken.None);
        var store = new KeyStateStore();
        var flag = new ForegroundFlag();
        var snapshot = withSnapshot ? Snapshot(Now(), overrides) : null;
        var engine = new EngineLoop(new Mapper(Forza(), store), snapshot, pad, flag);
        return new Rig(engine, driver, pad, store, flag);
    }

    [Fact]
    public void Output_is_neutral_until_the_game_has_focus_and_follows_the_keys_after()
    {
        var rig = Build(overrides: Depth(W, 0.5f));
        var now = Now();

        rig.Engine.Step(now);
        Assert.Equal(PadReport.Neutral, rig.Driver.State);

        // A fresh mapper hands over to the live depth on its fallback ramp: 0.5 in 25 ms.
        rig.Flag.IsGameForeground = true;
        for (var i = 1; i <= 40; i++)
        {
            rig.Engine.Step(now + i * OneMs);
        }
        Assert.InRange(rig.Driver.State.RightTrigger, 50, 150);
    }

    [Fact]
    public void Paused_output_is_neutral_and_resuming_starts_the_mapper_from_zero()
    {
        // No sensor: W falls back to the hook and ramps over 50 ms.
        var rig = Build(withSnapshot: false);
        rig.Flag.IsGameForeground = true;
        rig.Engine.Paused = true;
        rig.Store.SetDigital(W.Slot, true);
        var now = Now();

        for (var i = 0; i < 100; i++)
        {
            rig.Engine.Step(now + i * OneMs);
        }
        Assert.Equal(PadReport.Neutral, rig.Driver.State);

        rig.Engine.Paused = false;
        for (var i = 100; i < 110; i++)
        {
            rig.Engine.Step(now + i * OneMs);
        }

        // Ten live ticks from a reset ramp is 0.2, which the soft curve maps to about 19.
        // Without the reset the ramp, full after 100 ms of pause, would jump to 255.
        Assert.InRange(rig.Driver.State.RightTrigger, 10, 30);
    }

    [Fact]
    public void Buttons_follow_the_hook_while_live()
    {
        var rig = Build();
        rig.Flag.IsGameForeground = true;
        rig.Store.SetDigital(Space.Slot, true);

        rig.Engine.Step(Now());

        Assert.Equal(PadTarget.ButtonA.ButtonBit(), rig.Driver.State.Buttons);
    }

    [Fact]
    public void A_claimed_pad_ends_the_loop_without_a_submit()
    {
        var rig = Build(overrides: Depth(W, 1f));
        rig.Flag.IsGameForeground = true;
        var before = rig.Driver.Submits;

        rig.Pad.Claim();

        Assert.False(rig.Engine.Step(Now()));
        Assert.Equal(before, rig.Driver.Submits);
        Assert.Equal(0, rig.Engine.LastTickTicks);
    }

    [Fact]
    public void A_tick_allocates_nothing()
    {
        var rig = Build(overrides: Depth(W, 0.7f));
        rig.Driver.LogSubmits = false;
        rig.Store.SetDigital(Space.Slot, true);
        var now = Now();
        for (var i = 0; i < 100; i++)
        {
            rig.Flag.IsGameForeground = i % 6 < 3;
            rig.Engine.Step(now + i * OneMs);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 100; i < 1100; i++)
        {
            rig.Flag.IsGameForeground = i % 6 < 3;
            rig.Engine.Step(now + i * OneMs);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(rig.Driver.Submits > 100);
    }

    [Fact]
    public void The_thread_ticks_until_stopped_and_stops_within_its_bound()
    {
        var rig = Build();
        rig.Engine.Start();

        Assert.True(SpinWait.SpinUntil(() => rig.Engine.LastTickTicks != 0, 1000));
        var first = rig.Engine.LastTickTicks;
        Assert.True(SpinWait.SpinUntil(() => rig.Engine.LastTickTicks > first, 1000));
        var clock = Stopwatch.StartNew();

        Assert.True(rig.Engine.Stop());
        Assert.True(clock.ElapsedMilliseconds < EngineLoop.JoinTimeoutMs, $"Stop took {clock.ElapsedMilliseconds} ms.");
        Assert.False(rig.Engine.IsRunning);
        Assert.Null(rig.Engine.Fault);
    }

    [Fact]
    public void A_driver_failure_stops_the_engine_and_raises_the_reason_once()
    {
        var rig = Build(overrides: Depth(W, 1f));
        rig.Flag.IsGameForeground = true;
        rig.Driver.OnSubmit = _ => throw new VigemTargetNotPluggedInException();
        var raised = new List<string>();
        rig.Engine.Faulted += raised.Add;

        rig.Engine.Start();

        Assert.True(SpinWait.SpinUntil(() => !rig.Engine.IsRunning, 2000));
        Assert.Equal([VirtualPad.Describe(new VigemTargetNotPluggedInException())], raised);
        Assert.Equal(raised[0], rig.Engine.Fault);
        Assert.True(rig.Engine.Stop());
    }
}
