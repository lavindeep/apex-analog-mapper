using System.Diagnostics;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;
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

    /// <param name="Stamp">When the snapshot was stamped; tests tick from here so freshness is theirs to decide.</param>
    private sealed record Rig(EngineLoop Engine, FakePadDriver Driver, VirtualPad Pad, KeyStateStore Store, ForegroundFlag Flag, SensorSnapshot? Snapshot, long Stamp);

    private static Rig Build(bool withSnapshot = true, params (int Index, ushort Raw)[] overrides)
    {
        var driver = new FakePadDriver();
        var pad = VirtualPad.Connect(() => driver, CancellationToken.None);
        var store = new KeyStateStore();
        var flag = new ForegroundFlag();
        var stamp = Now();
        var snapshot = withSnapshot ? Snapshot(stamp, overrides) : null;
        var engine = new EngineLoop(new Mapper(Forza(), store), snapshot, pad, flag);
        return new Rig(engine, driver, pad, store, flag, snapshot, stamp);
    }

    [Fact]
    public void Output_is_neutral_until_the_game_has_focus_and_follows_the_keys_after()
    {
        var rig = Build(overrides: Depth(W, 0.5f));
        var now = rig.Stamp;

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
        var now = rig.Stamp;

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

    /// <summary>A fresh snapshot is republished every tick, so the measured ticks read live depth, not the fallback.</summary>
    [Fact]
    public void A_tick_allocates_nothing()
    {
        var rig = Build(overrides: Depth(W, 0.7f));
        rig.Driver.LogSubmits = false;
        rig.Store.SetDigital(Space.Slot, true);
        var working = Snapshot(rig.Stamp, Depth(W, 0.7f));
        var now = rig.Stamp;
        void Tick(int i)
        {
            var at = now + i * OneMs;
            rig.Flag.IsGameForeground = i % 6 < 3;
            working.Stamp(at);
            rig.Snapshot!.Publish(working);
            rig.Engine.Step(at);
        }
        for (var i = 0; i < 100; i++)
        {
            Tick(i);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 100; i < 1100; i++)
        {
            Tick(i);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(rig.Driver.Submits > 100);

        // The same fresh snapshot, held live: W settles at 0.7, which the soft curve maps to about 145.
        rig.Flag.IsGameForeground = true;
        for (var i = 1100; i < 1140; i++)
        {
            var at = now + i * OneMs;
            working.Stamp(at);
            rig.Snapshot!.Publish(working);
            rig.Engine.Step(at);
        }
        Assert.InRange(rig.Driver.State.RightTrigger, 120, 170);
    }

    [Fact]
    public void The_pad_s_presence_is_read_every_fifty_ms_on_the_engine_thread()
    {
        var rig = Build();
        var now = rig.Stamp;
        rig.Engine.Step(now);
        Assert.True(rig.Engine.PadPresent);

        rig.Driver.Gone = true;
        rig.Engine.Step(now + 10 * OneMs);
        Assert.True(rig.Engine.PadPresent);
        rig.Engine.Step(now + EngineLoop.PresenceCheckMs * OneMs);
        Assert.False(rig.Engine.PadPresent);
    }

    [Fact]
    public void The_thread_ticks_until_stopped_and_stops_within_its_bound()
    {
        var rig = Build();
        rig.Engine.Start();

        Assert.True(SpinWait.SpinUntil(() => rig.Engine.LastTickTicks != 0, 1000));
        var first = rig.Engine.LastTickTicks;
        Assert.True(SpinWait.SpinUntil(() => rig.Engine.LastTickTicks > first, 1000));

        // Stop joins with the engine's bound and says whether the thread exited within it.
        Assert.True(rig.Engine.Stop());
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
