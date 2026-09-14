using System.Collections.ObjectModel;
using ApexMapper.App.Services;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels.Profiles;

public sealed class BindingEditorViewModel : ObservableViewModel
{
    private readonly Profile _original;
    private string? _error;

    public BindingEditorViewModel(Profile profile)
    {
        _original = profile;
        Rows = new ObservableCollection<BindingEditorRow>(profile.SingleBindings
            .Select(binding => new BindingEditorRow(binding))
            .Concat(profile.AxisBindings.Select(binding => new BindingEditorRow(binding))));
        AddKeyCommand = new RelayCommand(() => Rows.Add(new BindingEditorRow(
            new SingleKeyBinding(default, BindingTarget.ButtonA, LinearCurve.Instance, 0, 0))
        {
            FirstKey = null,
        }));
        AddAxisCommand = new RelayCommand(() => Rows.Add(new BindingEditorRow(
            new AxisPairBinding(default, default, BindingTarget.LeftStickX,
                LinearCurve.Instance, 80, 80, SocdMode.Neutral))
        {
            FirstKey = null,
            SecondKey = null,
        }));
        RemoveCommand = new RelayCommand<BindingEditorRow>(row =>
        {
            if (row is not null) Rows.Remove(row);
        });
    }

    public string ProfileName => _original.Name;
    public ObservableCollection<BindingEditorRow> Rows { get; }
    public IRelayCommand AddKeyCommand { get; }
    public IRelayCommand AddAxisCommand { get; }
    public IRelayCommand<BindingEditorRow> RemoveCommand { get; }

    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }

    public bool TryBuildProfile(out Profile? profile)
    {
        profile = null;
        Error = null;
        for (var index = 0; index < Rows.Count; index++)
        {
            var row = Rows[index];
            if (row.FirstKey is null || (row.IsAxis && row.SecondKey is null))
                Error = $"Choose {(row.IsAxis ? "both keys" : "a key")} for binding {index + 1}.";
            else if ((row.FirstKey is { } first && MappingKeyRules.IsReserved(first))
                || (row.IsAxis && row.SecondKey is { } second && MappingKeyRules.IsReserved(second)))
                Error = $"Binding {index + 1}: {MappingKeyRules.ReservedKeyError}";
            else if (row.IsAxis && row.FirstKey == row.SecondKey)
                Error = $"Choose two different keys for binding {index + 1}.";
            else if (!row.TargetChoices.Any(choice => choice.Value == row.Target))
                Error = $"Choose a valid output for binding {index + 1}.";

            if (Error is not null) return false;
        }

        var duplicate = Rows.GroupBy(row => row.Target).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            Error = $"Use one binding for {BindingSummaryItem.TargetName(duplicate.Key)}.";
            return false;
        }

        profile = _original with
        {
            SingleBindings = Rows.Where(row => !row.IsAxis).Select(row => row.BuildSingle()).ToArray(),
            AxisBindings = Rows.Where(row => row.IsAxis).Select(row => row.BuildAxis()).ToArray(),
        };
        return true;
    }
}
