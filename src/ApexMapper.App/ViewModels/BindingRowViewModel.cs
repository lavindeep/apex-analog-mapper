using System.Runtime.CompilerServices;
using ApexMapper.App.Mvvm;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Keys;
using CoreResponse = ApexMapper.Core.Response.Response;

namespace ApexMapper.App.ViewModels;

public enum ResponsePreset
{
    Default,
    Custom,
}

/// <summary>A choice in one of the editor's lists, with the name it shows.</summary>
public sealed record Option<T>(T Value, string Name)
{
    /// <summary>What a screen reader says for the item.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// One binding in the profile editor: a key to a button or trigger, or two keys to a
/// stick axis, with its response and ramps. Holds what the user typed; the profile's
/// own validation runs when it is saved.
/// </summary>
public sealed class BindingRowViewModel : ObservableObject
{
    public static readonly IReadOnlyList<Option<PadTarget>> KeyTargets = [.. Enum.GetValues<PadTarget>().Where(t => !t.IsAxis()).Select(t => new Option<PadTarget>(t, NameOf(t)))];

    public static readonly IReadOnlyList<Option<PadTarget>> AxisTargets = [.. Enum.GetValues<PadTarget>().Where(t => t.IsAxis()).Select(t => new Option<PadTarget>(t, NameOf(t)))];

    public static readonly IReadOnlyList<ResponsePreset> Presets = Enum.GetValues<ResponsePreset>();

    public static readonly IReadOnlyList<Option<AxisMode>> Modes =
    [
        new(AxisMode.Position, "Position: how far the key is pressed sets how far the stick leans"),
        new(AxisMode.Rate, "Rate: how far the key is pressed sets how fast the stick moves"),
    ];

    public static readonly IReadOnlyList<Option<ConflictRule>> Conflicts =
    [
        new(ConflictRule.LastInputWins, "The key pressed last wins"),
        new(ConflictRule.Neutral, "The stick centres"),
    ];

    private readonly Func<ScanCode, string> _keyName;
    private readonly Action<BindingRowViewModel> _changed;
    private ScanCode _key;
    private ScanCode _negativeKey;
    private PadTarget _target;
    private double _exponent;
    private double _saturation;
    private double _deadzone;
    private bool _custom;
    private double _pressRampMs;
    private double _releaseRampMs;
    private AxisMode _mode;
    private ConflictRule _conflict;
    private double _rateMs;
    private double _returnMs;

    private BindingRowViewModel(bool isAxis, Func<ScanCode, string> keyName, Action<BindingRowViewModel> changed)
    {
        IsAxis = isAxis;
        _keyName = keyName;
        _changed = changed;
    }

    public static BindingRowViewModel From(KeyBinding binding, Func<ScanCode, string> keyName, Action<BindingRowViewModel> changed) =>
        new(false, keyName, changed)
        {
            _key = binding.Key,
            _target = binding.Target,
            _exponent = binding.Response.Exponent,
            _saturation = binding.Response.Saturation,
            _deadzone = binding.Response.Deadzone,
            _custom = binding.Response != CoreResponse.Default,
            _pressRampMs = binding.PressRampMs,
            _releaseRampMs = binding.ReleaseRampMs,
            _rateMs = AxisBinding.DefaultRateMs,
            _returnMs = AxisBinding.DefaultReturnMs,
        };

    public static BindingRowViewModel From(AxisBinding binding, Func<ScanCode, string> keyName, Action<BindingRowViewModel> changed) =>
        new(true, keyName, changed)
        {
            _negativeKey = binding.NegativeKey,
            _key = binding.PositiveKey,
            _target = binding.Target,
            _exponent = binding.Response.Exponent,
            _saturation = binding.Response.Saturation,
            _deadzone = binding.Response.Deadzone,
            _custom = binding.Response != CoreResponse.Default,
            _pressRampMs = binding.PressRampMs,
            _releaseRampMs = binding.ReleaseRampMs,
            _mode = binding.Mode,
            _conflict = binding.Conflict,
            _rateMs = binding.RateMs,
            _returnMs = binding.ReturnMs,
        };

    public bool IsAxis { get; }

    /// <summary>The key of a button or trigger binding; the positive key of an axis.</summary>
    public ScanCode Key
    {
        get => _key;
        set => Change(ref _key, value, [nameof(KeysText), nameof(KeyName), nameof(ChangeKeyText)]);
    }

    /// <summary>The negative key of an axis: left, or down.</summary>
    public ScanCode NegativeKey
    {
        get => _negativeKey;
        set => Change(ref _negativeKey, value, [nameof(KeysText), nameof(NegativeKeyName), nameof(ChangeNegativeKeyText)]);
    }

    public string KeyName => _keyName(_key);

    public string NegativeKeyName => _keyName(_negativeKey);

    /// <summary>The label of the button that captures <see cref="Key"/>, which is also its screen-reader name.</summary>
    public string ChangeKeyText => "Change " + KeyName;

    public string ChangeNegativeKeyText => "Change " + NegativeKeyName;

    public string KeysText => IsAxis ? $"{_keyName(_negativeKey)} and {_keyName(_key)}" : _keyName(_key);

    public bool Uses(ScanCode key) => _key == key || IsAxis && _negativeKey == key;

    public IReadOnlyList<Option<PadTarget>> Targets => IsAxis ? AxisTargets : KeyTargets;

    public PadTarget Target
    {
        get => _target;
        set => Change(ref _target, value, [nameof(TargetText), nameof(ShapesOutput)]);
    }

    public string TargetText => NameOf(_target);

    /// <summary>A button is on or off; only triggers and axes use the response and ramps.</summary>
    public bool ShapesOutput => !_target.IsButton();

    /// <summary>
    /// Default is the curve the app ships and can't be edited. Choosing Custom keeps the
    /// current values and shows the sliders; choosing Default puts its values back.
    /// </summary>
    public ResponsePreset Preset
    {
        get => _custom ? ResponsePreset.Custom : ResponsePreset.Default;
        set
        {
            if (value == Preset)
            {
                return;
            }
            _custom = value == ResponsePreset.Custom;
            if (_custom || CurrentResponse == CoreResponse.Default)
            {
                // The curve is the same until a slider moves, so there is nothing to save.
                Raise(nameof(Preset));
                Raise(nameof(IsCustom));
                return;
            }
            _exponent = CoreResponse.Default.Exponent;
            _saturation = CoreResponse.Default.Saturation;
            _deadzone = CoreResponse.Default.Deadzone;
            Changed(nameof(Exponent), nameof(Saturation), nameof(Deadzone), nameof(Preset), nameof(IsCustom));
        }
    }

    /// <summary>The curve's sliders show only for a Custom response.</summary>
    public bool IsCustom => _custom;

    /// <summary>1 is linear; above 1 is gentle at the start of travel, below 1 eager.</summary>
    public double Exponent
    {
        get => _exponent;
        set => ChangeCurve(ref _exponent, value);
    }

    /// <summary>The depth at which the output reaches full.</summary>
    public double Saturation
    {
        get => _saturation;
        set => ChangeCurve(ref _saturation, value);
    }

    /// <summary>The depth below which the output stays at zero.</summary>
    public double Deadzone
    {
        get => _deadzone;
        set => ChangeCurve(ref _deadzone, value);
    }

    public double PressRampMs
    {
        get => _pressRampMs;
        set => Change(ref _pressRampMs, value);
    }

    public double ReleaseRampMs
    {
        get => _releaseRampMs;
        set => Change(ref _releaseRampMs, value);
    }

    public AxisMode Mode
    {
        get => _mode;
        set => Change(ref _mode, value, [nameof(IsRateMode)]);
    }

    public bool IsRateMode => _mode == AxisMode.Rate;

    public ConflictRule Conflict
    {
        get => _conflict;
        set => Change(ref _conflict, value);
    }

    public double RateMs
    {
        get => _rateMs;
        set => Change(ref _rateMs, value);
    }

    public double ReturnMs
    {
        get => _returnMs;
        set => Change(ref _returnMs, value);
    }

    /// <summary>The response as typed, not yet validated.</summary>
    public CoreResponse CurrentResponse => new((float)_exponent, (float)_saturation, (float)_deadzone);

    public KeyBinding ToKeyBinding() => new(_key, _target, CurrentResponse, (float)_pressRampMs, (float)_releaseRampMs);

    /// <summary>What a screen reader says for the row in the bindings list.</summary>
    public override string ToString() => $"{KeysText}, {TargetText}";

    public AxisBinding ToAxisBinding() => new(
        _negativeKey, _key, _target, CurrentResponse, (float)_pressRampMs, (float)_releaseRampMs, _conflict, _mode, (float)_rateMs, (float)_returnMs);

    public static string NameOf(PadTarget target) => target switch
    {
        PadTarget.ButtonA => "A",
        PadTarget.ButtonB => "B",
        PadTarget.ButtonX => "X",
        PadTarget.ButtonY => "Y",
        PadTarget.LeftBumper => "Left bumper",
        PadTarget.RightBumper => "Right bumper",
        PadTarget.Start => "Start",
        PadTarget.Back => "Back",
        PadTarget.LeftStickClick => "Left stick click",
        PadTarget.RightStickClick => "Right stick click",
        PadTarget.Guide => "Guide",
        PadTarget.DpadUp => "D-pad up",
        PadTarget.DpadDown => "D-pad down",
        PadTarget.DpadLeft => "D-pad left",
        PadTarget.DpadRight => "D-pad right",
        PadTarget.LeftTrigger => "Left trigger",
        PadTarget.RightTrigger => "Right trigger",
        PadTarget.LeftStickX => "Left stick, left and right",
        PadTarget.LeftStickY => "Left stick, down and up",
        PadTarget.RightStickX => "Right stick, left and right",
        PadTarget.RightStickY => "Right stick, down and up",
        _ => target.ToString(),
    };

    /// <summary>An edited curve is Custom, even when the edit lands on Default's values.</summary>
    private void ChangeCurve(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (field != value)
        {
            _custom = true;
            Change(ref field, value, [nameof(Preset), nameof(IsCustom)], name);
        }
    }

    private void Change<T>(ref T field, T value, string[]? alsoChanged = null, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        Changed([name!, .. alsoChanged ?? []]);
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names)
        {
            Raise(name);
        }
        _changed(this);
    }
}
