using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ApexMapper.App.Mvvm;

/// <summary>Raises <see cref="PropertyChanged"/> for the bindings. View models live on the UI thread.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores the value and raises the change when it differs. Returns whether it did.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
