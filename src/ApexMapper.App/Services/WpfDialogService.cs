using System.Windows;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="IDialogService"/> backed by <see cref="System.Windows.MessageBox"/>.
/// Must be called on the WPF UI thread.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    public void ShowInfo(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;
}
