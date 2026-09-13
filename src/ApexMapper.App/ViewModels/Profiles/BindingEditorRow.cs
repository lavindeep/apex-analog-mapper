using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;

namespace ApexMapper.App.ViewModels.Profiles;

public sealed record BindingTargetChoice(BindingTarget Value, string Label);

public sealed class BindingEditorRow : ObservableViewModel
{
    private static readonly IReadOnlyList<BindingTargetChoice> AllTargets = Enum.GetValues<BindingTarget>()
        .Select(target => new BindingTargetChoice(target, BindingSummaryItem.TargetName(target)))
        .ToArray();
    private static readonly IReadOnlyList<BindingTargetChoice> AxisTargets = AllTargets
        .Where(choice => choice.Value is BindingTarget.LeftStickX or BindingTarget.LeftStickY
            or BindingTarget.RightStickX or BindingTarget.RightStickY)
        .ToArray();

    private readonly SingleKeyBinding? _single;
    private readonly AxisPairBinding? _axis;
    private KeyId? _firstKey;
    private KeyId? _secondKey;
    private BindingTarget _target;

    public BindingEditorRow(SingleKeyBinding binding)
    {
        _single = binding;
        _firstKey = binding.Source;
        _target = binding.Target;
    }

    public BindingEditorRow(AxisPairBinding binding)
    {
        _axis = binding;
        _firstKey = binding.NegativeKey;
        _secondKey = binding.PositiveKey;
        _target = binding.Target;
    }

    public bool IsAxis => _axis is not null;
    public IReadOnlyList<BindingTargetChoice> TargetChoices => IsAxis ? AxisTargets : AllTargets;

    public KeyId? FirstKey
    {
        get => _firstKey;
        set => SetProperty(ref _firstKey, value);
    }

    public KeyId? SecondKey
    {
        get => _secondKey;
        set => SetProperty(ref _secondKey, value);
    }

    public string? FirstDirection => !IsAxis ? null : Target switch
    {
        BindingTarget.LeftStickX or BindingTarget.RightStickX => "Left",
        BindingTarget.LeftStickY or BindingTarget.RightStickY => "Down",
        _ => null,
    };
    public string? SecondDirection => !IsAxis ? null : Target switch
    {
        BindingTarget.LeftStickX or BindingTarget.RightStickX => "Right",
        BindingTarget.LeftStickY or BindingTarget.RightStickY => "Up",
        _ => null,
    };

    public BindingTarget Target
    {
        get => _target;
        set
        {
            if (!SetProperty(ref _target, value)) return;
            OnPropertyChanged(nameof(FirstDirection));
            OnPropertyChanged(nameof(SecondDirection));
        }
    }

    internal SingleKeyBinding BuildSingle() => _single! with { Source = FirstKey!.Value, Target = Target };
    internal AxisPairBinding BuildAxis() => _axis! with
    {
        NegativeKey = FirstKey!.Value,
        PositiveKey = SecondKey!.Value,
        Target = Target,
    };
}
