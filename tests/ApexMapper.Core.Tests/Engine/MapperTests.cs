using ApexMapper.Core.Bindings;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Engine;

public class MapperTests
{
    private static readonly int WSlot = DefaultProfiles.Key.W.Slot;
    private static readonly int ASlot = DefaultProfiles.Key.A.Slot;
    private static readonly int DSlot = DefaultProfiles.Key.D.Slot;
    private static readonly int SpaceSlot = DefaultProfiles.Key.Space.Slot;
    private static readonly int LeftSlot = DefaultProfiles.Key.Left.Slot;

    private static (Mapper Mapper, KeyStateStore Store) Build(Profile? profile = null, KeyStateStore? store = null)
    {
        var compiled = CompiledProfile.TryCompile(profile ?? DefaultProfiles.Forza(), SensorMap.Default, Fixtures.Calibrations(), out _)!;
        store ??= new KeyStateStore();
        return (new Mapper(compiled, store), store);
    }

    [Fact]
    public void Full_profile_tick_maps_depth_and_buttons()
    {
        var (mapper, store) = Build();
        var report = PadReport.Neutral;

        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        Assert.Equal(PadReport.Neutral, report);

        var snapshot = Fixtures.Snapshot(10,
            (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.5f)),
            (Fixtures.A.SensorIndex, Fixtures.CountFor(Fixtures.A, 1f)));
        store.SetDigital(SpaceSlot, true);
        mapper.Tick(snapshot, 11, 1f, ref report);

        Fixtures.AssertTrigger(0.3299f, report.RightTrigger);
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

        var held = Fixtures.Snapshot(0, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.8f)));
        mapper.Tick(held, 1, 1f, ref report);
        Assert.Equal(0, report.RightTrigger);
        Assert.True(store.IsGated(WSlot));

        // With the sensor alive, a hook key-up is not a release (rapid trigger).
        store.SetDigital(WSlot, false);
        mapper.Tick(held, 2, 1f, ref report);
        Assert.True(store.IsGated(WSlot));
        Assert.Equal(0, report.RightTrigger);

        mapper.Tick(Fixtures.Snapshot(3), 4, 1f, ref report);
        Assert.False(store.IsGated(WSlot));
        Assert.Equal(0, report.RightTrigger);

        mapper.Tick(held, 5, 1f, ref report);
        Fixtures.AssertTrigger(Core.Response.Response.Soft.Map(0.8f), report.RightTrigger);
    }

    [Fact]
    public void Sensor_fault_mid_press_ramps_to_full_in_fifteen_ms_and_recovery_ramps_back_to_the_live_depth()
    {
        var (mapper, store) = Build(LinearForza());
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        var pressed = Fixtures.Snapshot(0, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.7f)));
        store.SetDigital(WSlot, true);
        mapper.Tick(pressed, 2, 1f, ref report);
        Fixtures.AssertTrigger(0.7f, report.RightTrigger);

        // Snapshot goes stale at 60 ms. Digital says down: ramp from 0.7 to 1.0 at 50 ms full scale.
        mapper.Tick(pressed, 65, 5f, ref report);
        Assert.False(mapper.SnapshotFresh);
        Assert.False(store.IsGated(WSlot));
        Assert.Equal(4, mapper.FallbackCount);
        Fixtures.AssertTrigger(0.8f, report.RightTrigger);
        mapper.Tick(pressed, 70, 5f, ref report);
        Fixtures.AssertTrigger(0.9f, report.RightTrigger);
        mapper.Tick(pressed, 75, 5f, ref report);
        Assert.Equal(255, report.RightTrigger);
        mapper.Tick(pressed, 80, 5f, ref report);
        Assert.Equal(255, report.RightTrigger);

        // Sensor returns while W is still held at 0.7: the output sweeps down to it, no step, no stickiness.
        var back = Fixtures.Snapshot(90, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.7f)));
        mapper.Tick(back, 91, 5f, ref report);
        Assert.True(mapper.SnapshotFresh);
        Assert.Equal(0, mapper.FallbackCount);
        Fixtures.AssertTrigger(0.9f, report.RightTrigger);
        mapper.Tick(back, 96, 5f, ref report);
        Fixtures.AssertTrigger(0.8f, report.RightTrigger);
        mapper.Tick(back, 101, 5f, ref report);
        Fixtures.AssertTrigger(0.7f, report.RightTrigger);
        // Converged: the live depth passes straight through from here.
        var deeper = Fixtures.Snapshot(102, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.95f)));
        mapper.Tick(deeper, 103, 1f, ref report);
        Fixtures.AssertTrigger(0.95f, report.RightTrigger);
    }

    [Fact]
    public void Release_under_fallback_ramps_down_and_recovery_at_rest_finishes_the_ramp()
    {
        var (mapper, store) = Build(LinearForza());
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        var pressed = Fixtures.Snapshot(0, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 1f)));
        store.SetDigital(WSlot, true);
        mapper.Tick(pressed, 2, 1f, ref report);
        Assert.Equal(255, report.RightTrigger);

        mapper.Tick(pressed, 65, 5f, ref report);
        store.SetDigital(WSlot, false);
        mapper.Tick(pressed, 90, 25f, ref report);
        Fixtures.AssertTrigger(0.5f, report.RightTrigger);

        // Sensor back and W at rest: the ramp finishes at the same rate, no step.
        mapper.Tick(Fixtures.Snapshot(95), 96, 5f, ref report);
        Assert.Equal(0, mapper.FallbackCount);
        Fixtures.AssertTrigger(0.4f, report.RightTrigger);
        mapper.Tick(Fixtures.Snapshot(95), 116, 20f, ref report);
        Assert.Equal(0, report.RightTrigger);
        mapper.Tick(Fixtures.Snapshot(117, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.3f))), 118, 1f, ref report);
        Fixtures.AssertTrigger(0.3f, report.RightTrigger);
    }

    [Fact]
    public void Dead_sensor_at_start_still_drives_analog_keys_from_the_hook()
    {
        var (mapper, store) = Build(LinearForza());
        store.GateUnknown();
        var report = PadReport.Neutral;
        mapper.Tick(null, 1, 1f, ref report);
        Assert.True(store.IsGated(WSlot));
        Assert.Equal(4, mapper.FallbackCount);

        // The first press is a transition from a known-up key, so it clears the gate.
        store.SetDigital(WSlot, true);
        mapper.Tick(null, 2, 5f, ref report);
        Assert.False(store.IsGated(WSlot));
        Fixtures.AssertTrigger(0.1f, report.RightTrigger);
        mapper.Tick(null, 3, 45f, ref report);
        Assert.Equal(255, report.RightTrigger);
        store.SetDigital(WSlot, false);
        mapper.Tick(null, 4, 50f, ref report);
        Assert.Equal(0, report.RightTrigger);
    }

    [Fact]
    public void A_fresh_snapshot_missing_a_group_falls_back_only_the_keys_in_it()
    {
        var (mapper, store) = Build(LinearForza());
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);

        var groupTwoOnly = Fixtures.Snapshot(2, [2], (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.6f)));
        store.SetDigital(ASlot, true);
        mapper.Tick(groupTwoOnly, 3, 50f, ref report);
        Assert.True(mapper.SnapshotFresh);
        // A, S and D live in group 3.
        Assert.Equal(3, mapper.FallbackCount);
        Assert.True(float.IsNaN(store.Read(ASlot).Analog));
        Fixtures.AssertTrigger(0.6f, report.RightTrigger);
        Assert.Equal(-32767, report.LeftStickX);
    }

    [Fact]
    public void Gate_during_a_ramp_restarts_the_ramp_from_zero()
    {
        // The left arrow has no sensor, so it is digital with the binding's own 100 ms ramps.
        var profile = DefaultProfiles.Forza() with
        {
            Keys = [new(DefaultProfiles.Key.Left, PadTarget.RightTrigger, Core.Response.Response.Linear, 100f, 100f)],
            Axes = [],
        };
        var (mapper, store) = Build(profile);
        var report = PadReport.Neutral;
        store.SetDigital(LeftSlot, true);
        mapper.Tick(null, 1, 30f, ref report);
        mapper.Tick(null, 2, 30f, ref report);
        Fixtures.AssertTrigger(0.6f, report.RightTrigger);

        store.Gate(LeftSlot);
        mapper.Tick(null, 2, 1f, ref report);
        Assert.Equal(0, report.RightTrigger);

        store.SetDigital(LeftSlot, false);
        mapper.Tick(null, 3, 1f, ref report);
        store.SetDigital(LeftSlot, true);
        mapper.Tick(null, 4, 40f, ref report);
        Fixtures.AssertTrigger(0.4f, report.RightTrigger);
    }

    [Fact]
    public void Buttons_are_digital_and_ignore_ramp_and_response()
    {
        var profile = DefaultProfiles.Forza() with
        {
            Keys = [new(DefaultProfiles.Key.Space, PadTarget.ButtonA, Core.Response.Response.Soft, 200f, 200f)],
            Axes = [],
        };
        var (mapper, store) = Build(profile);
        var report = PadReport.Neutral;
        store.SetDigital(SpaceSlot, true);
        mapper.Tick(null, 1, 1f, ref report);
        Assert.Equal(PadTarget.ButtonA.ButtonBit(), report.Buttons);
        store.SetDigital(SpaceSlot, false);
        mapper.Tick(null, 2, 1f, ref report);
        Assert.Equal(0, report.Buttons);
        store.Gate(SpaceSlot);
        store.SetDigital(SpaceSlot, true);
        mapper.Tick(null, 3, 1f, ref report);
        Assert.Equal(PadTarget.ButtonA.ButtonBit(), report.Buttons);
    }

    [Fact]
    public void Last_input_wins_on_the_steering_axis()
    {
        var (mapper, _) = Build(LinearForza());
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        var aHeld = Fixtures.Snapshot(0, (Fixtures.A.SensorIndex, Fixtures.CountFor(Fixtures.A, 0.8f)));
        mapper.Tick(aHeld, 2, 1f, ref report);
        Fixtures.AssertStick(-0.8f, report.LeftStickX);
        var both = Fixtures.Snapshot(0,
            (Fixtures.A.SensorIndex, Fixtures.CountFor(Fixtures.A, 0.8f)),
            (Fixtures.D.SensorIndex, Fixtures.CountFor(Fixtures.D, 0.4f)));
        mapper.Tick(both, 3, 1f, ref report);
        Fixtures.AssertStick(0.4f, report.LeftStickX);
    }

    [Fact]
    public void Rate_mode_integrates_steering()
    {
        var (mapper, _) = Build(RateForza(100f, 50f));
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        var dHalf = Fixtures.Snapshot(0, (Fixtures.D.SensorIndex, Fixtures.CountFor(Fixtures.D, 0.5f)));
        mapper.Tick(dHalf, 2, 50f, ref report);
        Fixtures.AssertStick(0.25f, report.LeftStickX);
        mapper.Tick(dHalf, 3, 50f, ref report);
        Fixtures.AssertStick(0.5f, report.LeftStickX);
        mapper.Tick(Fixtures.Snapshot(0), 4, 25f, ref report);
        Assert.Equal(0, report.LeftStickX);
    }

    [Fact]
    public void Gating_a_rate_axis_zeroes_it_at_once()
    {
        var (mapper, store) = Build(RateForza(100f, 500f));
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        var dFull = Fixtures.Snapshot(0, (Fixtures.D.SensorIndex, Fixtures.CountFor(Fixtures.D, 1f)));
        mapper.Tick(dFull, 2, 50f, ref report);
        mapper.Tick(dFull, 3, 50f, ref report);
        Assert.Equal(32767, report.LeftStickX);

        store.GateUnknown();
        mapper.Tick(dFull, 4, 1f, ref report);
        Assert.Equal(0, report.LeftStickX);
        Assert.Equal(0, report.RightStickX);
    }

    [Fact]
    public void Gating_one_key_of_a_digital_axis_lets_the_other_steer_from_zero()
    {
        var forza = LinearForza();
        var camera = forza.Axes[1] with { Mode = AxisMode.Rate, RateMs = 100f, ReturnMs = 500f };
        var (mapper, store) = Build(forza with { Axes = [forza.Axes[0], camera, forza.Axes[2]] });
        var report = PadReport.Neutral;
        store.SetDigital(LeftSlot, true);
        mapper.Tick(Fixtures.Snapshot(0), 1, 50f, ref report);
        Fixtures.AssertStick(-0.5f, report.RightStickX);

        // Left is held at alt-tab: gated. Right is pressed afterwards and steers from zero.
        store.GateUnknown();
        store.SetDigital(DefaultProfiles.Key.Right.Slot, true);
        mapper.Tick(Fixtures.Snapshot(0), 2, 20f, ref report);
        Fixtures.AssertStick(0.2f, report.RightStickX);
    }

    [Fact]
    public void Stale_snapshot_never_sets_the_gate()
    {
        var (mapper, store) = Build();
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        mapper.Tick(Fixtures.Snapshot(0), 200, 1f, ref report);
        Assert.False(mapper.SnapshotFresh);
        Assert.False(store.IsGated(WSlot));
        Assert.False(store.IsGated(ASlot));
    }

    [Fact]
    public void A_stall_integrates_at_most_fifty_ms_and_a_backwards_clock_holds()
    {
        var (mapper, store) = Build(RateForza(100f, 500f));
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        var dFull = Fixtures.Snapshot(0, (Fixtures.D.SensorIndex, Fixtures.CountFor(Fixtures.D, 1f)));
        mapper.Tick(dFull, 2, 500f, ref report);
        Fixtures.AssertStick(0.5f, report.LeftStickX);
        mapper.Tick(dFull, 3, -5f, ref report);
        Fixtures.AssertStick(0.5f, report.LeftStickX);

        store.SetDigital(WSlot, true);
        mapper.Tick(Fixtures.Snapshot(0), 100, 0f, ref report);
        Assert.False(mapper.SnapshotFresh);
        Assert.Equal(0, report.RightTrigger);
    }

    [Fact]
    public void Reset_state_forgets_ramps_and_deflection()
    {
        var (mapper, store) = Build(RateForza(100f, 500f));
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        var dFull = Fixtures.Snapshot(0, (Fixtures.D.SensorIndex, Fixtures.CountFor(Fixtures.D, 1f)));
        store.SetDigital(WSlot, true);
        mapper.Tick(dFull, 2, 50f, ref report);
        mapper.Tick(dFull, 100, 20f, ref report);
        Assert.NotEqual(0, report.LeftStickX);
        Assert.NotEqual(0, report.RightTrigger);

        mapper.ResetState();
        store.SetDigital(WSlot, false);
        mapper.Tick(Fixtures.Snapshot(101), 102, 1f, ref report);
        Assert.Equal(PadReport.Neutral, report);
    }

    [Fact]
    public void Rebuilding_a_mapper_over_a_live_store_drops_the_old_analog_flags()
    {
        var (_, store) = Build();
        var digitalOnly = DefaultProfiles.Forza() with
        {
            Keys = [new(DefaultProfiles.Key.W, PadTarget.ButtonA, Core.Response.Response.Linear, 0f, 0f)],
            Axes = [],
        };
        var (mapper, _) = Build(digitalOnly, store);
        Assert.False(store.Read(WSlot).AnalogDriven);

        store.SetDigital(WSlot, true);
        store.GateUnknown();
        store.SetDigital(WSlot, false);
        Assert.False(store.IsGated(WSlot));
        var report = PadReport.Neutral;
        store.SetDigital(WSlot, true);
        mapper.Tick(null, 1, 1f, ref report);
        Assert.Equal(PadTarget.ButtonA.ButtonBit(), report.Buttons);
    }

    [Fact]
    public void Spans_are_never_shared_between_keys()
    {
        var (mapper, _) = Build(LinearForza());
        var report = PadReport.Neutral;
        mapper.Tick(Fixtures.Snapshot(0), 1, 1f, ref report);
        // The same raw count on W (span 3217) and S (span 2712) is a different depth.
        mapper.Tick(Fixtures.Snapshot(0, (Fixtures.W.SensorIndex, 2500), (Fixtures.S.SensorIndex, 2500)), 2, 1f, ref report);
        Fixtures.AssertTrigger((2500 - 878 - 20) / 3197f, report.RightTrigger);
        Fixtures.AssertTrigger((2500 - 847 - 20) / 2692f, report.LeftTrigger);
        Assert.NotEqual(report.RightTrigger, report.LeftTrigger);
    }

    [Fact]
    public void Tick_allocates_nothing()
    {
        var (mapper, store) = Build();
        var report = PadReport.Neutral;
        var snapshot = Fixtures.Snapshot(0, (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.5f)));
        store.SetDigital(SpaceSlot, true);
        for (var i = 0; i < 1000; i++)
        {
            mapper.Tick(snapshot, 1, 1f, ref report);
        }
        Assert.True(mapper.SnapshotFresh);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            mapper.Tick(snapshot, 1, 1f, ref report);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Tick_allocates_nothing_on_the_fallback_gated_rate_and_every_target_paths()
    {
        var forza = RateForza(100f, 50f);
        var everyTarget = forza with
        {
            Keys =
            [
                new(DefaultProfiles.Key.W, PadTarget.RightTrigger, Core.Response.Response.Soft, 0f, 0f),
                new(DefaultProfiles.Key.S, PadTarget.LeftTrigger, Core.Response.Response.Soft, 0f, 0f),
                new(new ScanCode(0x39), PadTarget.ButtonA, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x30), PadTarget.ButtonB, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x2D), PadTarget.ButtonX, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x15), PadTarget.ButtonY, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x2A), PadTarget.LeftBumper, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x0F), PadTarget.RightBumper, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x1C), PadTarget.Start, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x0E), PadTarget.Back, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x22), PadTarget.LeftStickClick, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x23), PadTarget.RightStickClick, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x24), PadTarget.Guide, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x02), PadTarget.DpadUp, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x03), PadTarget.DpadDown, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x04), PadTarget.DpadLeft, Core.Response.Response.Linear, 0f, 0f),
                new(new ScanCode(0x05), PadTarget.DpadRight, Core.Response.Response.Linear, 0f, 0f),
            ],
            Axes =
            [
                forza.Axes[0],
                new(new ScanCode(0x3B), new ScanCode(0x3C), PadTarget.LeftStickY, Core.Response.Response.Linear, 30f, 30f, ConflictRule.Neutral, AxisMode.Position, 150f, 100f),
                forza.Axes[1] with { Mode = AxisMode.Rate },
                forza.Axes[2],
            ],
        };
        Assert.Null(everyTarget.Validate());
        var (mapper, store) = Build(everyTarget);
        var report = PadReport.Neutral;
        var fresh = Fixtures.Snapshot(0,
            (Fixtures.W.SensorIndex, Fixtures.CountFor(Fixtures.W, 0.5f)),
            (Fixtures.A.SensorIndex, Fixtures.CountFor(Fixtures.A, 0.5f)),
            (Fixtures.D.SensorIndex, Fixtures.CountFor(Fixtures.D, 0.3f)));
        var stale = Fixtures.Snapshot(-1000);
        var rest = Fixtures.Snapshot(0);
        foreach (var binding in everyTarget.Keys)
        {
            store.SetDigital(binding.Key.Slot, true);
        }
        store.SetDigital(0x3B, true);
        store.SetDigital(0x3C, true);
        store.SetDigital(LeftSlot, true);

        void Cycle(int i)
        {
            switch (i % 8)
            {
                case 0: mapper.Tick(fresh, 1, 1f, ref report); break;
                case 1: mapper.Tick(stale, 1, 1f, ref report); break;
                case 2: mapper.Tick(null, 1, 1f, ref report); break;
                case 3: store.Gate(WSlot); store.Gate(DSlot); mapper.Tick(fresh, 1, 1f, ref report); break;
                case 4: mapper.Tick(rest, 1, 1f, ref report); break;
                case 5: mapper.Tick(fresh, 1, 1f, ref report); break;
                case 6: mapper.Tick(fresh, 1, 0f, ref report); break;
                default: mapper.Tick(stale, 1, 500f, ref report); break;
            }
        }

        for (var i = 0; i < 1000; i++)
        {
            Cycle(i);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            Cycle(i);
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

    private static Profile RateForza(float rateMs, float returnMs)
    {
        var forza = LinearForza();
        var steering = forza.Axes[0] with { Mode = AxisMode.Rate, RateMs = rateMs, ReturnMs = returnMs };
        return forza with { Axes = [steering, forza.Axes[1], forza.Axes[2]] };
    }
}
