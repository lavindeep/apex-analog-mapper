using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete ITrayService backed by H.NotifyIcon.Wpf's TaskbarIcon.
/// Must be created and used on the WPF UI thread.
/// </summary>
public sealed class TrayService : ITrayService, ITrayServiceInternal, IDisposable
{
    private readonly TaskbarIcon _icon;
    private bool _disposed;

    public event EventHandler? OpenMainWindowRequested;
    public event EventHandler? ExitRequested;

    public TrayService(TaskbarIcon icon)
    {
        _icon = icon ?? throw new ArgumentNullException(nameof(icon));
        _icon.TrayLeftMouseDown += OnTrayLeftMouseDown;
    }

    public void Show() => _icon.Visibility = System.Windows.Visibility.Visible;

    public void Hide() => _icon.Visibility = System.Windows.Visibility.Collapsed;

    public void SetEnabled(bool enabled)
    {
        _icon.ToolTipText = enabled ? "Apex Analog Mapper (enabled)" : "Apex Analog Mapper (disabled)";
    }

    public void SetTooltip(string text) => _icon.ToolTipText = text;

    public void ShowBalloon(string title, string message)
        => _icon.ShowNotification(title, message, NotificationIcon.Info);

    // ITrayServiceInternal — invoked by TrayMenuViewModel.ExitCommand
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnTrayLeftMouseDown(object sender, System.Windows.RoutedEventArgs e)
        => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.TrayLeftMouseDown -= OnTrayLeftMouseDown;
        _icon.Dispose();
    }
}
