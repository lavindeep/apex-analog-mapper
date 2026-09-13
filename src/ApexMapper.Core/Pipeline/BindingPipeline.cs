using System.Diagnostics;
using ApexMapper.Core.Diagnostics;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Ramps;
using ApexMapper.Core.Socd;

namespace ApexMapper.Core.Pipeline;

/// <summary>
/// Evaluates all bindings each tick into a <see cref="VirtualPadState"/>.
/// </summary>
/// <remarks>
/// The per-binding stage order is <b>ramp → deadzone/curve → SOCD</b>, which is deliberately not
/// the spec's literal deadzone → curve → ramp wording. Ramps run first so the shaping curve sees a
/// continuous 0..1 signal: a digital key only ever reports 0 or 1, and a curve applied to a bare
/// 0/1 step would be a no-op (it can only remap the two endpoints). Ramping first turns the step
/// into a smooth ramp the curve can actually shape, then SOCD resolves the two ramped/shaped sides
/// of an axis pair into a single signed value.
/// Analog input bypasses press ramps. Its optional release ramp runs after
/// shaping and SOCD, while gates and conflicting directions remain immediate.
/// </remarks>
public sealed class BindingPipeline
{
    private static readonly double TicksToMicros = 1_000_000.0 / Stopwatch.Frequency;

    private readonly SingleKeyBinding[] _singles;
    private readonly AxisPairBinding[] _axes;
    private readonly Ramp[] _singleRamps;
    private readonly Ramp[] _axisNegRamps;
    private readonly Ramp[] _axisPosRamps;
    private readonly SocdState[] _axisSocd;
    private readonly AnalogReturn[] _singleAnalogReturns;
    private readonly AnalogReturn[] _axisAnalogReturns;
    private readonly byte[] _axisAnalogSources;
    private long _storeResetGeneration;

    public BindingPipeline(
        IReadOnlyList<SingleKeyBinding> singles,
        IReadOnlyList<AxisPairBinding> axes)
    {
        _singles = singles.ToArray();
        _axes = axes.ToArray();
        _singleRamps = _singles.Select(b => new Ramp(b.PressRampMs, b.ReleaseRampMs)).ToArray();
        _axisNegRamps = _axes.Select(b => new Ramp(b.PressRampMs, b.ReleaseRampMs)).ToArray();
        _axisPosRamps = _axes.Select(b => new Ramp(b.PressRampMs, b.ReleaseRampMs)).ToArray();
        _axisSocd = new SocdState[_axes.Length];
        _singleAnalogReturns = new AnalogReturn[_singles.Length];
        _axisAnalogReturns = new AnalogReturn[_axes.Length];
        _axisAnalogSources = new byte[_axes.Length];
    }

    /// <summary>
    /// Latency recorder for diagnostics. Defaults to a Null recorder whose
    /// <see cref="LatencyRecorder.IsActive"/> is false; the pipeline skips
    /// timestamp work in that case. Replace via initializer when Phase 5
    /// diagnostics are wired up.
    /// <para>
    /// Interim measurement anchors: what is recorded today is the duration of
    /// <see cref="Tick"/> itself (pipeline compute time), NOT the spec's
    /// end-to-end "input-event to IPC-submit" latency. The start anchor moves
    /// to the input-event timestamp and the end anchor to IPC submission once
    /// the supervisor IPC path exists; until then the histogram must not be
    /// presented as end-to-end latency.
    /// </para>
    /// </summary>
    public LatencyRecorder Latency { get; init; } = LatencyRecorder.Null;

    public void Tick(KeyStateStore store, float dtMs, ref VirtualPadState pad)
    {
        var latency = Latency;
        var measure = latency.IsActive;
        long startTicks = measure ? Stopwatch.GetTimestamp() : 0;

        pad.Reset();
        var resetGeneration = store.ResetGeneration;
        if (resetGeneration != _storeResetGeneration)
        {
            ResetAnalogReturns();
            _storeResetGeneration = resetGeneration;
        }

        for (var i = 0; i < _singles.Length; i++)
        {
            var b = _singles[i];
            var state = store.Get(b.Source);
            var value = ResolveValue(state, _singleRamps[i], dtMs);
            var shaped = b.Curve.Map(value);
            if (state.Source == KeyProvenance.Analog
                && b.Target is BindingTarget.LeftStickX or BindingTarget.LeftStickY
                    or BindingTarget.RightStickX or BindingTarget.RightStickY
                    or BindingTarget.LeftTrigger or BindingTarget.RightTrigger
                && !store.IsGated(b.Source))
                shaped = ReturnAnalog(ref _singleAnalogReturns[i], shaped, b.ReleaseRampMs, dtMs);
            else
                _singleAnalogReturns[i] = default;
            ApplyTarget(b.Target, shaped, ref pad);
        }

        TickAxes(store, dtMs, ref pad);

        if (measure)
        {
            latency.Record((long)((Stopwatch.GetTimestamp() - startTicks) * TicksToMicros));
        }
    }

    private void TickAxes(KeyStateStore store, float dtMs, ref VirtualPadState pad)
    {
        for (var i = 0; i < _axes.Length; i++)
        {
            var b = _axes[i];
            var negState = store.Get(b.NegativeKey);
            var posState = store.Get(b.PositiveKey);

            var negValue = ResolveValue(negState, _axisNegRamps[i], dtMs);
            var posValue = ResolveValue(posState, _axisPosRamps[i], dtMs);

            var negShaped = b.Curve.Map(negValue);
            var posShaped = b.Curve.Map(posValue);

            var signed = SocdResolver.Resolve(b.Socd, negShaped, posShaped, ref _axisSocd[i]);
            var sources = (byte)((negState.Source == KeyProvenance.Analog ? 1 : 0)
                | (posState.Source == KeyProvenance.Analog ? 2 : 0));
            if (sources != _axisAnalogSources[i]) _axisAnalogReturns[i] = default;
            _axisAnalogSources[i] = sources;
            var analog = sources != 0
                && (negState.Source == KeyProvenance.Analog || negValue == 0f)
                && (posState.Source == KeyProvenance.Analog || posValue == 0f);
            var neutralConflict = signed == 0f && negShaped > 0f && posShaped > 0f;
            if (analog && !neutralConflict && !store.IsGated(b.NegativeKey) && !store.IsGated(b.PositiveKey))
                signed = ReturnAnalog(ref _axisAnalogReturns[i], signed, b.ReleaseRampMs, dtMs);
            else
                _axisAnalogReturns[i] = default;
            switch (b.Target)
            {
                case BindingTarget.LeftStickX: pad.LeftStickX = signed; break;
                case BindingTarget.LeftStickY: pad.LeftStickY = signed; break;
                case BindingTarget.RightStickX: pad.RightStickX = signed; break;
                case BindingTarget.RightStickY: pad.RightStickY = signed; break;
                default: break;
            }
        }
    }

    private static float ResolveValue(KeyState state, Ramp ramp, float dtMs)
    {
        if (state.Source == KeyProvenance.Analog)
        {
            ramp.Reset();
            return state.Value;
        }
        ramp.Update(state.Value > 0.5f, dtMs);
        return ramp.Value;
    }

    // Sensor increases and direction changes stay immediate. Only return
    // motion is limited, after shaping and SOCD, in full-scale units per ms.
    private struct AnalogReturn
    {
        public float Value;
        public float Target;
    }

    private static float ReturnAnalog(ref AnalogReturn history, float target, float releaseMs, float dtMs)
    {
        var value = target;
        if (releaseMs > 0f && float.IsFinite(releaseMs) && float.IsFinite(dtMs)
            && MathF.Abs(target) < MathF.Abs(history.Value)
            && MathF.Abs(target) <= MathF.Abs(history.Target) && history.Value * target >= 0f)
        {
            var magnitude = MathF.Max(MathF.Abs(target), MathF.Abs(history.Value) - MathF.Max(0f, dtMs) / releaseMs);
            value = MathF.CopySign(magnitude, history.Value);
        }
        history.Value = value;
        history.Target = target;
        return value;
    }

    internal void Reset()
    {
        foreach (var ramp in _singleRamps) ramp.Reset();
        foreach (var ramp in _axisNegRamps) ramp.Reset();
        foreach (var ramp in _axisPosRamps) ramp.Reset();
        Array.Clear(_axisSocd);
        ResetAnalogReturns();
    }

    internal void ResetAnalogReturns()
    {
        Array.Clear(_singleAnalogReturns);
        Array.Clear(_axisAnalogReturns);
        Array.Clear(_axisAnalogSources);
    }

    private static void ApplyTarget(BindingTarget target, float value, ref VirtualPadState pad)
    {
        var pressed = value >= 0.5f;
        switch (target)
        {
            case BindingTarget.LeftStickX: pad.LeftStickX = value; break;
            case BindingTarget.LeftStickY: pad.LeftStickY = value; break;
            case BindingTarget.RightStickX: pad.RightStickX = value; break;
            case BindingTarget.RightStickY: pad.RightStickY = value; break;
            case BindingTarget.LeftTrigger: pad.LeftTrigger = value; break;
            case BindingTarget.RightTrigger: pad.RightTrigger = value; break;
            case BindingTarget.ButtonA: pad.ButtonA = pressed; break;
            case BindingTarget.ButtonB: pad.ButtonB = pressed; break;
            case BindingTarget.ButtonX: pad.ButtonX = pressed; break;
            case BindingTarget.ButtonY: pad.ButtonY = pressed; break;
            case BindingTarget.ButtonLB: pad.ButtonLB = pressed; break;
            case BindingTarget.ButtonRB: pad.ButtonRB = pressed; break;
            case BindingTarget.ButtonStart: pad.ButtonStart = pressed; break;
            case BindingTarget.ButtonBack: pad.ButtonBack = pressed; break;
            case BindingTarget.ButtonLS: pad.ButtonLS = pressed; break;
            case BindingTarget.ButtonRS: pad.ButtonRS = pressed; break;
            case BindingTarget.ButtonGuide: pad.ButtonGuide = pressed; break;
            case BindingTarget.DpadUp: pad.DpadUp = pressed; break;
            case BindingTarget.DpadDown: pad.DpadDown = pressed; break;
            case BindingTarget.DpadLeft: pad.DpadLeft = pressed; break;
            case BindingTarget.DpadRight: pad.DpadRight = pressed; break;
        }
    }
}
