using System.Windows.Input;

namespace ApexMapper.App.Mvvm;

/// <summary>A button's action and whether it is available. The owner calls <see cref="Refresh"/> when the answer may have changed.</summary>
public sealed class Command(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
