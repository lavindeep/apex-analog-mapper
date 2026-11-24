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
        new(AxisMode.Position, "Position"),
        new(AxisMode.Rate, "Rate"),
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
    private bool _isExpanded;
    private bool _isCapturing;

    private BindingRowViewModel(bool isAxis, Func<ScanCode, string> keyName, Action<BindingRowViewModel> changed)
    {
        IsAxis = isAxis;
        _keyName = keyName;
        _changed = changed;
    }

    public static BindingRowViewModel From(KeyBinding binding, Func<ScanCode, string> keyName, Action<BindingRowViewModel> changed) =>
        new BindingRowViewModel(false, keyName, changed)
        {
            _key = binding.Key,
            _target = binding.Target,
            _exponent = binding.Response.Exponent,
            _saturation = binding.Response.Saturation,
            _deadzone = binding.Response.Deadzone,
            _pressRampMs = binding.PressRampMs,
            _releaseRampMs = binding.ReleaseRampMs,
            _rateMs = AxisBinding.DefaultRateMs,
            _returnMs = AxisBinding.DefaultReturnMs,
        }.WithPreset();

    public static BindingRowViewModel From(AxisBinding binding, Func<ScanCode, string> keyName, Action<BindingRowViewModel> changed) =>
        new BindingRowViewModel(true, keyName, changed)
        {
            _negativeKey = binding.NegativeKey,
            _key = binding.PositiveKey,
            _target = binding.Target,
            _exponent = binding.Response.Exponent,
            _saturation = binding.Response.Saturation,
            _deadzone = binding.Response.Deadzone,
            _pressRampMs = binding.PressRampMs,
            _releaseRampMs = binding.ReleaseRampMs,
            _mode = binding.Mode,
            _conflict = binding.Conflict,
            _rateMs = binding.RateMs,
            _returnMs = binding.ReturnMs,
        }.WithPreset();

    public bool IsAxis { get; }

    /// <summary>The row is open in the list. Closing a row set to Custom with Default's settings shows it as Default again.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && !value && _custom && MatchesDefault)
            {
                _custom = false;
                Raise(nameof(Preset));
                Raise(nameof(IsCustom));
                Raise(nameof(PresetText));
                Raise(nameof(Summary));
                Raise(nameof(CustomTag));
            }
        }
    }

    /// <summary>The next key press goes to this row.</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        internal set => Set(ref _isCapturing, value);
    }

    /// <summary>What a screen reader says for the row: its keys, what they press, and a Custom preset.</summary>
    public string Summary => ToString() + (CustomTag is null ? "" : ", Custom");

    /// <summary>The header's mark for a trigger or stick on a Custom preset, or null.</summary>
    public string? CustomTag => _custom && ShapesOutput ? "Custom" : null;

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
    /// <summary>A stick's two buttons name their direction, since their order alone does not say it.</summary>
    public string ChangeKeyText => IsAxis ? $"Change {DirectionsOf(_target).Positive} key ({KeyName})" : "Change " + KeyName;

    public string ChangeNegativeKeyText => $"Change {DirectionsOf(_target).Negative} key ({NegativeKeyName})";

    public string KeysText => IsAxis ? $"{_keyName(_negativeKey)} and {_keyName(_key)}" : _keyName(_key);

    public bool Uses(ScanCode key) => _key == key || IsAxis && _negativeKey == key;

    public IReadOnlyList<Option<PadTarget>> Targets => IsAxis ? AxisTargets : KeyTargets;

    public PadTarget Target
    {
        get => _target;
        set => Change(ref _target, value, [nameof(TargetText), nameof(ShapesOutput), nameof(NegativeKeyHint), nameof(KeyHint), nameof(ChangeKeyText), nameof(ChangeNegativeKeyText)]);
    }

    /// <summary>A stick axis's two directions, as the prompts and buttons name them.</summary>
    public static (string Negative, string Positive) DirectionsOf(PadTarget target) =>
        target is PadTarget.LeftStickY or PadTarget.RightStickY ? ("down", "up") : ("left", "right");

    public string NegativeKeyHint => $"The key for {DirectionsOf(_target).Negative}";

    public string? KeyHint => IsAxis ? $"The key for {DirectionsOf(_target).Positive}" : null;

    public string TargetText => NameOf(_target);

    /// <summary>A button is on or off; only triggers and axes use the response and ramps.</summary>
    public bool ShapesOutput => !_target.IsButton();

    /// <summary>
    /// Default is every setting the app ships, and can't be edited: the curve, no ramps,
    /// and for a stick, position movement with the last key pressed winning. Choosing
    /// Custom keeps the current values and shows them; choosing Default puts its values back.
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
            if (_custom || MatchesDefault)
            {
                // The settings are the same until one changes, so there is nothing to save.
                Raise(nameof(Preset));
                Raise(nameof(IsCustom));
                Raise(nameof(PresetText));
                Raise(nameof(Summary));
                Raise(nameof(CustomTag));
                return;
            }
            _exponent = CoreResponse.Default.Exponent;
            _saturation = CoreResponse.Default.Saturation;
            _deadzone = CoreResponse.Default.Deadzone;
            _pressRampMs = 0;
            _releaseRampMs = 0;
            _mode = AxisMode.Position;
            _conflict = ConflictRule.LastInputWins;
            _rateMs = AxisBinding.DefaultRateMs;
            _returnMs = AxisBinding.DefaultReturnMs;
            Changed(nameof(Exponent), nameof(Saturation), nameof(Deadzone), nameof(PressRampMs), nameof(ReleaseRampMs), nameof(Mode), nameof(IsRateMode), nameof(OutputName), nameof(GraphName), nameof(SaturationName), nameof(ModeText),
                nameof(Conflict), nameof(RateMs), nameof(ReturnMs), nameof(Preset), nameof(IsCustom), nameof(PresetText));
        }
    }

    /// <summary>The settings show only for a Custom preset.</summary>
    public bool IsCustom => _custom;

    /// <summary>What the chosen preset does, under its list.</summary>
    public string PresetText => _custom ? "Change any setting for this binding. Choose Default to put back the settings the app ships."
        : IsAxis ? "The settings the app ships, tested driving Forza Horizon 6. The stick leans as far as a key is pressed, rising quickly from " +
            "the start of the press with no dead zone or delay. With both keys held, the one pressed last wins."
        : "The settings the app ships, tested driving Forza Horizon 6. The output rises quickly from the start of the press, with no dead zone or delay.";

    /// <summary>1 is linear; above 1 is gentle at the start of travel, below 1 eager.</summary>
    public double Exponent
    {
        get => _exponent;
        set => ChangeTuning(ref _exponent, value);
    }

    /// <summary>The depth at which the output reaches full.</summary>
    public double Saturation
    {
        get => _saturation;
        set => ChangeTuning(ref _saturation, value);
    }

    /// <summary>The depth below which the output stays at zero.</summary>
    public double Deadzone
    {
        get => _deadzone;
        set => ChangeTuning(ref _deadzone, value);
    }

    public double PressRampMs
    {
        get => _pressRampMs;
        set => ChangeTuning(ref _pressRampMs, value);
    }

    public double ReleaseRampMs
    {
        get => _releaseRampMs;
        set => ChangeTuning(ref _releaseRampMs, value);
    }

    public AxisMode Mode
    {
        get => _mode;
        set => ChangeTuning(ref _mode, value, [nameof(IsRateMode), nameof(OutputName), nameof(GraphName), nameof(SaturationName), nameof(ModeText)]);
    }

    public bool IsRateMode => _mode == AxisMode.Rate;

    /// <summary>What the preview's height shows: the output, or in Rate mode how fast the stick moves.</summary>
    public string OutputName => IsAxis && IsRateMode ? "Stick speed" : "Output";

    /// <summary>Why the response would not save, shown under its sliders as they move, or null.</summary>
    public string? DeadzoneProblem => ShapesOutput && Deadzone >= Saturation
        ? $"The dead zone has to end before {FullName}."
        : null;

    /// <summary>What the preview shows in place of a curve it cannot draw, or null.</summary>
    public string? EmptyGraphText => DeadzoneProblem is null ? null : $"No curve until the dead zone ends before {FullName}";

    private string FullName => IsAxis && IsRateMode ? "full speed" : "full output";

    /// <summary>Where the Curve slider sits, in the words under it, for screen readers.</summary>
    public string CurveText => Exponent switch
    {
        <= CoreResponse.MinExponent => "Eager",
        < 1 => "Between Eager and Straight",
        1 => "Straight",
        < CoreResponse.MaxExponent => "Between Straight and Gentle",
        _ => "Gentle",
    };

    public string ModeText => IsRateMode
        ? "How far the key is pressed sets how fast the stick moves. Released keys bring it back to centre."
        : "How far the key is pressed sets how far the stick leans.";

    /// <summary>The graph's screen-reader name.</summary>
    public string GraphName => $"The {OutputName.ToLowerInvariant()} for how far the key is pressed";

    /// <summary>The saturation slider's label: where the output, or the stick's speed, is full.</summary>
    public string SaturationName => IsAxis && IsRateMode ? "Full speed at" : "Full output at";

    public ConflictRule Conflict
    {
        get => _conflict;
        set => ChangeTuning(ref _conflict, value);
    }

    public double RateMs
    {
        get => _rateMs;
        set => ChangeTuning(ref _rateMs, value);
    }

    public double ReturnMs
    {
        get => _returnMs;
        set => ChangeTuning(ref _returnMs, value);
    }

    /// <summary>The response as typed, not yet validated.</summary>
    public CoreResponse CurrentResponse => new((float)_exponent, (float)_saturation, (float)_deadzone);

    /// <summary>A button ignores its response and hides it, so one that would not save goes back to Default.</summary>
    public KeyBinding ToKeyBinding() => new(_key, _target,
        ShapesOutput || CoreResponse.Validate(CurrentResponse.Exponent, CurrentResponse.Saturation, CurrentResponse.Deadzone) is null ? CurrentResponse : CoreResponse.Default,
        (float)_pressRampMs, (float)_releaseRampMs);

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

    /// <summary>An edited setting is Custom, even when the edit lands on Default's values.</summary>
    private void ChangeTuning<T>(ref T field, T value, string[]? alsoChanged = null, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            _custom = true;
            Change(ref field, value, [nameof(Preset), nameof(IsCustom), nameof(PresetText), .. alsoChanged ?? []], name);
        }
    }

    private bool MatchesDefault => CurrentResponse == CoreResponse.Default && _pressRampMs == 0 && _releaseRampMs == 0
        && (!IsAxis || _mode == AxisMode.Position && _conflict == ConflictRule.LastInputWins && _rateMs == AxisBinding.DefaultRateMs && _returnMs == AxisBinding.DefaultReturnMs);

    private BindingRowViewModel WithPreset()
    {
        _custom = !MatchesDefault;
        return this;
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
        Raise(nameof(Summary));
        Raise(nameof(CustomTag));
        Raise(nameof(DeadzoneProblem));
        Raise(nameof(EmptyGraphText));
        Raise(nameof(CurveText));
        _changed(this);
    }
}
