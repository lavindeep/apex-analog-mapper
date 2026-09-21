using ApexMapper.Core.Bindings;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;

namespace ApexMapper.Core.Engine;

/// <summary>
/// The per-tick mapping from key state to a pad report. Owns every piece of per-key
/// and per-axis state in arrays sized at construction, so a tick allocates nothing.
/// One mapper serves one session: it claims the store's analog-driven flags when built
/// and the session builds a new one for the next start.
///
/// Order per key: gate, source, ramp, response. Then per axis: conflict, mode, pack.
/// A gated key contributes zero and has its state reset, so clearing the gate never
/// releases a stale value. A key's source is the sensor whenever its reading is
/// available, otherwise the hook. The handover from hook to sensor ramps the output
/// from where the fallback left it to the live depth, so a sensor that comes back
/// mid-press never steps and never sticks.
/// </summary>
public sealed class Mapper
{
    /// <summary>Fallback ramp when the binding's own ramp is zero: a dead sensor must not step the output.</summary>
    public const float FallbackRampMs = 50f;

    /// <summary>Longest tick the engine integrates; a stall beyond it moves the output by at most this much.</summary>
    public const float MaxTickMs = 50f;

    private struct KeyState
    {
        public Ramp Ramp;
        public bool Analog;
        public bool Converging;
    }

    private struct AxisState
    {
        public ConflictState Conflict;
        public RateState Rate;
        public bool Gated;
    }

    private readonly CompiledProfile _profile;
    private readonly KeyStateStore _store;
    private readonly KeyState[] _keyStates;
    private readonly KeyState[] _axisNegativeStates;
    private readonly KeyState[] _axisPositiveStates;
    private readonly AxisState[] _axisStates;
    private readonly float[] _depths;
    private int _fallbackCount;
    private int _snapshotFresh;

    public Mapper(CompiledProfile profile, KeyStateStore store)
    {
        _profile = profile;
        _store = store;
        _keyStates = new KeyState[profile.Keys.Count];
        _axisNegativeStates = new KeyState[profile.Axes.Count];
        _axisPositiveStates = new KeyState[profile.Axes.Count];
        _axisStates = new AxisState[profile.Axes.Count];
        _depths = new float[profile.AnalogKeys.Count];
        store.ClearAnalogDriven();
        foreach (var key in profile.AnalogKeys)
        {
            store.SetAnalogDriven(key.Slot, true);
        }
    }

    /// <summary>True when the last tick read a fresh snapshot. Safe to read from another thread.</summary>
    public bool SnapshotFresh => Volatile.Read(ref _snapshotFresh) != 0;

    /// <summary>Analog-driven keys whose reading was unavailable on the last tick, gated or not. Safe to read from another thread.</summary>
    public int FallbackCount => Volatile.Read(ref _fallbackCount);

    /// <summary>Forget all ramp, handover, conflict and rate state, as on session start or return from alt-tab.</summary>
    public void ResetState()
    {
        Array.Clear(_keyStates);
        Array.Clear(_axisNegativeStates);
        Array.Clear(_axisPositiveStates);
        Array.Clear(_axisStates);
        Volatile.Write(ref _fallbackCount, 0);
    }

    public void Tick(SensorSnapshot? snapshot, long nowTicks, float dtMs, ref PadReport report)
    {
        dtMs = float.IsFinite(dtMs) ? Math.Clamp(dtMs, 0f, MaxTickMs) : 0f;
        var fresh = ApplySnapshot(snapshot, nowTicks);
        Volatile.Write(ref _snapshotFresh, fresh ? 1 : 0);

        report = PadReport.Neutral;
        var fallback = 0;

        var keys = _profile.Keys;
        for (var i = 0; i < keys.Count; i++)
        {
            var binding = keys[i];
            if (binding.Target.IsTrigger())
            {
                var shaped = Resolve(binding.Key, ref _keyStates[i], dtMs, ref fallback, out _);
                report.SetTrigger(binding.Target, shaped);
            }
            else
            {
                // Buttons are digital: no ramp, no response curve, never sensor-driven.
                var slot = _store.Read(binding.Key.Slot);
                report.SetButton(binding.Target, slot.Digital && !slot.Gated);
            }
        }

        var axes = _profile.Axes;
        for (var i = 0; i < axes.Count; i++)
        {
            var binding = axes[i];
            ref var axis = ref _axisStates[i];
            var negative = Resolve(binding.Negative, ref _axisNegativeStates[i], dtMs, ref fallback, out var negativeGated);
            var positive = Resolve(binding.Positive, ref _axisPositiveStates[i], dtMs, ref fallback, out var positiveGated);
            var gated = negativeGated || positiveGated;
            if (gated && !axis.Gated)
            {
                axis.Conflict.Reset();
                axis.Rate.Reset();
            }
            axis.Gated = gated;
            var signed = axis.Conflict.Resolve(binding.Conflict, negative, positive);
            var value = binding.Mode == AxisMode.Rate
                ? axis.Rate.Step(signed, dtMs, binding.RateMs, binding.ReturnMs)
                : signed;
            report.SetAxis(binding.Target, value);
        }

        Volatile.Write(ref _fallbackCount, fallback);
    }

    /// <summary>
    /// Copies each analog key's depth into the store. Reads under the snapshot's
    /// sequence lock into a scratch array first, so a torn read is retried rather than
    /// committed (a torn zero would clear a gate). Returns whether the snapshot was fresh.
    /// </summary>
    private bool ApplySnapshot(SensorSnapshot? snapshot, long nowTicks)
    {
        var analogKeys = _profile.AnalogKeys;
        var fresh = false;
        var read = false;
        if (snapshot is not null)
        {
            for (var attempt = 0; attempt < SensorSnapshot.MaxReadAttempts && !read; attempt++)
            {
                if (!snapshot.TryBeginRead(out var generation))
                {
                    continue;
                }
                fresh = snapshot.IsFresh(nowTicks);
                for (var i = 0; i < analogKeys.Count; i++)
                {
                    var cal = analogKeys[i].Calibration!;
                    _depths[i] = fresh && snapshot.WasRead(cal.SensorIndex)
                        ? Normalizer.Depth(cal, snapshot.Raw[cal.SensorIndex])
                        : float.NaN;
                }
                read = snapshot.EndRead(generation);
            }
        }
        if (!read)
        {
            fresh = false;
            Array.Fill(_depths, float.NaN);
        }
        for (var i = 0; i < analogKeys.Count; i++)
        {
            _store.SetAnalog(analogKeys[i].Slot, _depths[i]);
        }
        return fresh;
    }

    private float Resolve(CompiledKey key, ref KeyState state, float dtMs, ref int fallback, out bool gated)
    {
        var slot = _store.Read(key.Slot);
        var analogAvailable = key.AnalogDriven && !float.IsNaN(slot.Analog);
        if (key.AnalogDriven && !analogAvailable)
        {
            fallback++;
        }
        gated = slot.Gated;
        if (gated)
        {
            state = default;
            return 0f;
        }

        float value;
        if (analogAvailable)
        {
            if (!state.Analog)
            {
                state.Analog = true;
                state.Converging = state.Ramp.Value != slot.Analog;
            }
            if (state.Converging)
            {
                state.Ramp.Update(slot.Analog, dtMs, RampFor(key, slot.Analog > state.Ramp.Value, true));
                state.Converging = state.Ramp.Value != slot.Analog;
            }
            else
            {
                state.Ramp.Seed(slot.Analog);
            }
            value = state.Ramp.Value;
        }
        else
        {
            state.Analog = false;
            state.Converging = false;
            var source = slot.Digital ? 1f : 0f;
            state.Ramp.Update(source, dtMs, RampFor(key, source > 0f, key.AnalogDriven));
            value = state.Ramp.Value;
        }
        return key.Response.Map(value);
    }

    /// <summary>The binding's ramp for the direction, or the fallback ramp when the binding has none and the key is sensor-driven.</summary>
    private static float RampFor(CompiledKey key, bool rising, bool useFallback)
    {
        var duration = rising ? key.PressRampMs : key.ReleaseRampMs;
        return duration <= 0f && useFallback ? FallbackRampMs : duration;
    }
}
