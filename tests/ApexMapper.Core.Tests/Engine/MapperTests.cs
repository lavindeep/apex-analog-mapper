using ApexMapper.Core.Bindings;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Engine;

public class MapperTests
{
    private const long Limit = 1_000_000;
    private static readonly int WSlot = DefaultProfiles.Key.W.Slot;
    private static readonly int ASlot = DefaultProfiles.Key.A.Slot;
    private static readonly int SpaceSlot = DefaultProfiles.Key.Space.Slot;

    private static (Mapper Mapper, KeyStateStore Store) Build(Profile? profile = null)
    {
        var compiled = CompiledProfile.TryCompile(profile ?? DefaultProfiles.Forza(), SensorMap.Default, Fixtures.Calibrations(), out _)!;
        var store = new KeyStateStore();
        return (new Mapper(compiled, store), store);
    }

    [Fact]
    public void Full_profile_tick_maps_depth_and_buttons()
    {
        var (mapper, store) = Build();
        var report = PadReport.Neutral;

        // Everything at rest first so the analog latches.
        mapper.Tick(Fixtures.Snapshot(0, Limit), 1, 1f, ref report);
        Assert.Equal(PadReport.Neutral, report);

        var snapshot = Fixtures.Snapshot(10, Limit,
            (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.5f)),
            (Fixtures.A.SensorIndex, Fixtures.CountFor(Fixtures.A, 1f)));
        store.SetDigital(SpaceSlot, true);
        mapper.Tick(snapshot, 11, 1f, ref report);

        Fixtures.AssertTrigger(Core.Response.Response.Soft.Map(0.5f), report.RightTrigger);
        Assert.Equal(0, report.LeftTrigger);
        Assert.Equal(-32767, report.LeftStickX);
        Assert.Equal(PadTarget.ButtonA.ButtonBit(), report.Buttons);
        Assert.True(mapper.SnapshotFresh);
        Assert.Equal(0, mapper.FallbackCount);
    }

    [Fact]
    public void Keys_held_at_start_stay_dead_until_released_once()
    {
        var (mapper, store) = Build();
        store.SetDigital(WSlot, true);
        store.GateUnknown();
        var report = PadReport.Neutral;

        var held = Fixtures.Snapshot(0, Limit, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.8f)));
        mapper.Tick(held, 1, 1f, ref report);
        Assert.Equal(0, report.RightTrigger);
        Assert.True(store.IsGated(WSlot));

        mapper.Tick(Fixtures.Snapshot(2, Limit), 3, 1f, ref report);
        Assert.False(store.IsGated(WSlot));
        Assert.Equal(0, report.RightTrigger);

        mapper.Tick(held, 4, 1f, ref report);
        Fixtures.AssertTrigger(Core.Response.Response.Soft.Map(0.8f), report.RightTrigger);
    }

    [Fact]
    public void Sensor_fault_mid_press_ramps_from_the_last_depth_and_recovers_at_rest()
    {
        var (mapper, store) = Build(LinearForza());
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0, Limit), 1, 1f, ref report);
        var pressed = Fixtures.Snapshot(0, Limit, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.7f)));
        store.SetDigital(WSlot, true);
        mapper.Tick(pressed, 2, 1f, ref report);
        Fixtures.AssertTrigger(0.7f, report.RightTrigger);

        // Snapshot goes stale: same snapshot, clock past its limit. Digital says down.
        mapper.Tick(pressed, Limit + 10, 5f, ref report);
        Assert.False(mapper.SnapshotFresh);
        // Every analog key is in fallback while the snapshot is stale, not only the held one.
        Assert.Equal(4, mapper.FallbackCount);
        Fixtures.AssertTrigger(0.8f, report.RightTrigger);
        mapper.Tick(pressed, Limit + 20, 5f, ref report);
        Fixtures.AssertTrigger(0.9f, report.RightTrigger);
        mapper.Tick(pressed, Limit + 30, 20f, ref report);
        Assert.Equal(255, report.RightTrigger);

        // Release under fallback ramps down at the same rate.
        store.SetDigital(WSlot, false);
        mapper.Tick(pressed, Limit + 40, 25f, ref report);
        Fixtures.AssertTrigger(0.5f, report.RightTrigger);

        // Sensor returns while the key is still down: stays digital.
        store.SetDigital(WSlot, true);
        var back = Fixtures.Snapshot(Limit + 50, Limit, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.3f)));
        mapper.Tick(back, Limit + 51, 100f, ref report);
        Assert.Equal(255, report.RightTrigger);
        Assert.Equal(1, mapper.FallbackCount);

        // Read at rest: analog takes over from zero.
        store.SetDigital(WSlot, false);
        mapper.Tick(Fixtures.Snapshot(Limit + 60, Limit), Limit + 61, 100f, ref report);
        Assert.Equal(0, report.RightTrigger);
        Assert.Equal(0, mapper.FallbackCount);
        mapper.Tick(Fixtures.Snapshot(Limit + 70, Limit, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.3f))), Limit + 71, 1f, ref report);
        Fixtures.AssertTrigger(0.3f, report.RightTrigger);
    }

    [Fact]
    public void Gate_during_a_ramp_restarts_the_ramp_from_zero()
    {
        var profile = DefaultProfiles.Forza() with
        {
            Keys = [new(DefaultProfiles.Key.Space, PadTarget.RightBumper, Core.Response.Response.Linear, 100f, 100f)],
            Axes = [],
        };
        var (mapper, store) = Build(profile);
        var report = PadReport.Neutral;
        store.SetDigital(SpaceSlot, true);
        // A button reads pressed at 0.5; watch the ramp via a trigger-style check instead:
        // 60 ms into a 100 ms ramp the internal value is 0.6, so the button is pressed.
        mapper.Tick(null, 1, 60f, ref report);
        Assert.Equal(PadTarget.RightBumper.ButtonBit(), report.Buttons);

        store.Gate(SpaceSlot);
        mapper.Tick(null, 2, 1f, ref report);
        Assert.Equal(0, report.Buttons);

        // Release clears the gate; a new press ramps from zero, so 40 ms in it is not yet pressed.
        store.SetDigital(SpaceSlot, false);
        mapper.Tick(null, 3, 1f, ref report);
        store.SetDigital(SpaceSlot, true);
        mapper.Tick(null, 4, 40f, ref report);
        Assert.Equal(0, report.Buttons);
        mapper.Tick(null, 5, 20f, ref report);
        Assert.Equal(PadTarget.RightBumper.ButtonBit(), report.Buttons);
    }

    [Fact]
    public void Last_input_wins_on_the_steering_axis()
    {
        var (mapper, _) = Build(LinearForza());
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0, Limit), 1, 1f, ref report);
        var aHeld = Fixtures.Snapshot(0, Limit, (Fixtures.A.SensorIndex, Fixtures.CountFor(Fixtures.A, 0.8f)));
        mapper.Tick(aHeld, 2, 1f, ref report);
        Fixtures.AssertStick(-0.8f, report.LeftStickX);
        var both = Fixtures.Snapshot(0, Limit,
            (Fixtures.A.SensorIndex, Fixtures.CountFor(Fixtures.A, 0.8f)),
            (Fixtures.D.SensorIndex, Fixtures.CountFor(Fixtures.D, 0.4f)));
        mapper.Tick(both, 3, 1f, ref report);
        Fixtures.AssertStick(0.4f, report.LeftStickX);
    }

    [Fact]
    public void Rate_mode_integrates_steering()
    {
        var forza = LinearForza();
        var steering = forza.Axes[0] with { Mode = AxisMode.Rate, RateMs = 100f, ReturnMs = 50f };
        var profile = forza with { Axes = [steering, forza.Axes[1], forza.Axes[2]] };
        var (mapper, _) = Build(profile);
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0, Limit), 1, 1f, ref report);
        var dHalf = Fixtures.Snapshot(0, Limit, (Fixtures.D.SensorIndex, Fixtures.CountFor(Fixtures.D, 0.5f)));
        mapper.Tick(dHalf, 2, 50f, ref report);
        Fixtures.AssertStick(0.25f, report.LeftStickX);
        mapper.Tick(dHalf, 3, 50f, ref report);
        Fixtures.AssertStick(0.5f, report.LeftStickX);
        mapper.Tick(Fixtures.Snapshot(0, Limit), 4, 25f, ref report);
        Assert.Equal(0, report.LeftStickX);
    }

    [Fact]
    public void Tick_allocates_nothing()
    {
        var (mapper, store) = Build();
        var report = PadReport.Neutral;
        var snapshot = Fixtures.Snapshot(0, Limit, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.5f)));
        store.SetDigital(SpaceSlot, true);
        for (var i = 0; i < 1000; i++)
        {
            mapper.Tick(snapshot, i, 1f, ref report);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            mapper.Tick(snapshot, i, 1f, ref report);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static Profile LinearForza()
    {
        var forza = DefaultProfiles.Forza();
        return forza with
        {
            Keys = forza.Keys.Select(k => k with { Response = Core.Response.Response.Linear }).ToList(),
            Axes = forza.Axes.Select(a => a with { Response = Core.Response.Response.Linear }).ToList(),
        };
    }
}
