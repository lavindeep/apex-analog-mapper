using ApexMapper.Core.Keys;
using ApexMapper.Core.Ramps;
using ApexMapper.Core.Socd;

namespace ApexMapper.Core.Pipeline;

public sealed class BindingPipeline
{
    private readonly SingleKeyBinding[] _singles;
    private readonly AxisPairBinding[] _axes;
    private readonly Ramp[] _singleRamps;
    private readonly Ramp[] _axisNegRamps;
    private readonly Ramp[] _axisPosRamps;
    private readonly SocdState[] _axisSocd;

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
    }

    public void Tick(KeyStateStore store, float dtMs, ref VirtualPadState pad)
    {
        pad.Reset();

        for (var i = 0; i < _singles.Length; i++)
        {
            var b = _singles[i];
            var state = store.Get(b.Source);
            var value = ResolveValue(state, _singleRamps[i], dtMs);
            var shaped = b.Curve.Map(value);
            ApplyTarget(b.Target, shaped, ref pad);
        }

        TickAxes(store, dtMs, ref pad);
    }

    private void TickAxes(KeyStateStore store, float dtMs, ref VirtualPadState pad)
    {
        // Filled in Task 16.
    }

    private static float ResolveValue(KeyState state, Ramp ramp, float dtMs)
    {
        if (state.Source == KeyProvenance.Analog)
        {
            return state.Value;
        }
        ramp.Update(state.Value > 0.5f, dtMs);
        return ramp.Value;
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
