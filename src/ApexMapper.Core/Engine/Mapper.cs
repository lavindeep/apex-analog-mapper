using ApexMapper.Core.Bindings;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;

namespace ApexMapper.Core.Engine;

/// <summary>
/// The per-tick mapping from key state to a pad report. Owns every piece of per-key
/// state (selector, ramp, conflict, rate) in arrays sized at construction, so a tick
/// allocates nothing.
///
/// Order per key: gate, source select, ramp, response. Then per axis: conflict, mode,
/// pack. A gated key contributes zero and has its state reset, so clearing the gate
/// never releases a stale value.
/// </summary>
public sealed class Mapper
{
    /// <summary>Fallback ramp when the binding's own ramp is zero: a dead sensor must not step the output.</summary>
    public const float FallbackRampMs = 50f;

    private struct KeyState
    {
        public SourceSelector Selector;
        public Ramp Ramp;
    }

    private readonly CompiledProfile _profile;
    private readonly KeyStateStore _store;
    private readonly KeyState[] _keyStates;
    private readonly KeyState[] _axisNegativeStates;
    private readonly KeyState[] _axisPositiveStates;
    private readonly ConflictState[] _conflicts;
    private readonly RateState[] _rates;
    private int _fallbackCount;

    public Mapper(CompiledProfile profile, KeyStateStore store)
    {
        _profile = profile;
        _store = store;
        _keyStates = new KeyState[profile.Keys.Count];
        _axisNegativeStates = new KeyState[profile.Axes.Count];
        _axisPositiveStates = new KeyState[profile.Axes.Count];
        _conflicts = new ConflictState[profile.Axes.Count];
        _rates = new RateState[profile.Axes.Count];
        foreach (var key in profile.AnalogKeys)
        {
            store.SetAnalogDriven(key.Slot, true);
        }
    }

    /// <summary>True when the last tick had the sensor snapshot fresh enough to drive analog keys.</summary>
    public bool SnapshotFresh { get; private set; }

    /// <summary>Analog-driven keys that the hook drove on the last tick.</summary>
    public int FallbackCount => _fallbackCount;

    /// <summary>Forget all ramp and latch state, as on session start or return from alt-tab.</summary>
    public void ResetState()
    {
        Array.Clear(_keyStates);
        Array.Clear(_axisNegativeStates);
        Array.Clear(_axisPositiveStates);
        Array.Clear(_conflicts);
        Array.Clear(_rates);
        _fallbackCount = 0;
    }

    public void Tick(SensorSnapshot? snapshot, long nowTicks, float dtMs, ref PadReport report)
    {
        SnapshotFresh = snapshot is not null && snapshot.IsFresh(nowTicks);
        ApplySnapshot(snapshot);

        report = PadReport.Neutral;
        _fallbackCount = 0;

        var keys = _profile.Keys;
        for (var i = 0; i < keys.Count; i++)
        {
            var binding = keys[i];
            var shaped = Resolve(binding.Key, ref _keyStates[i], dtMs);
            if (binding.Target.IsTrigger())
            {
                report.SetTrigger(binding.Target, shaped);
            }
            else
            {
                report.SetButton(binding.Target, shaped >= 0.5f);
            }
        }

        var axes = _profile.Axes;
        for (var i = 0; i < axes.Count; i++)
        {
            var binding = axes[i];
            var negative = Resolve(binding.Negative, ref _axisNegativeStates[i], dtMs);
            var positive = Resolve(binding.Positive, ref _axisPositiveStates[i], dtMs);
            var signed = _conflicts[i].Resolve(binding.Conflict, negative, positive);
            var value = binding.Mode == AxisMode.Rate
                ? _rates[i].Step(signed, dtMs, binding.RateMs, binding.ReturnMs)
                : signed;
            report.SetAxis(binding.Target, value);
        }
    }

    private void ApplySnapshot(SensorSnapshot? snapshot)
    {
        var analogKeys = _profile.AnalogKeys;
        for (var i = 0; i < analogKeys.Count; i++)
        {
            var key = analogKeys[i];
            var cal = key.Calibration!;
            var depth = SnapshotFresh && snapshot!.WasRead(cal.SensorIndex)
                ? Normalizer.Depth(cal, snapshot.Raw[cal.SensorIndex])
                : float.NaN;
            _store.SetAnalog(key.Slot, depth);
        }
    }

    private float Resolve(CompiledKey key, ref KeyState state, float dtMs)
    {
        var slot = _store.Read(key.Slot);
        if (slot.Gated)
        {
            state.Selector.Reset();
            state.Ramp.Reset();
            return 0f;
        }

        var source = state.Selector.Select(in slot, analogAvailable: key.AnalogDriven && SnapshotFresh);
        float value;
        if (state.Selector.UsingAnalog)
        {
            state.Ramp.Seed(source);
            value = source;
        }
        else
        {
            var fallback = key.AnalogDriven;
            if (fallback)
            {
                _fallbackCount++;
            }
            var duration = source > 0f ? key.PressRampMs : key.ReleaseRampMs;
            if (duration <= 0f && fallback)
            {
                duration = FallbackRampMs;
            }
            state.Ramp.Update(source, dtMs, duration);
            value = state.Ramp.Value;
        }
        return key.Response.Map(value);
    }
}
