using System.Windows;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="IDialogService"/> backed by <see cref="System.Windows.MessageBox"/>.
/// <para>
/// Callable from any thread: <see cref="System.Windows.MessageBox"/> forwards to
/// the Win32 user32 MessageBox, which runs its own modal message loop, so it does
/// not require the WPF dispatcher. The enable flow deliberately calls
/// <see cref="Confirm"/> from a worker thread (the tray toggle runs the enable
/// off the UI thread). The only caveat is ownership: shown off the UI thread the
/// dialog is unowned, so it is not modal to the main window and appears as an
/// independent top-level window — acceptable for this app's tray-driven prompts.
/// </para>
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
