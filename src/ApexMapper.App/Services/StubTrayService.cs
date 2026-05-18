namespace ApexMapper.App.Services;

/// <summary>
/// No-op <see cref="ITrayService"/> and <see cref="ITrayServiceInternal"/> used
/// in DI resolution tests that run without a live WPF Application.
/// Not for production use — the composition root in App.xaml.cs replaces this
/// with the real <see cref="TrayService"/> backed by H.NotifyIcon.
/// </summary>
internal sealed class StubTrayService : ITrayService, ITrayServiceInternal
{
    public event EventHandler? OpenMainWindowRequested;
    public event EventHandler? ExitRequested;

    public void Show()  { }
    public void Hide()  { }
    public void SetEnabled(bool enabled)   { }
    public void SetTooltip(string text)    { }
    public void ShowBalloon(string title, string message) { }
    public void RequestOpenMainWindow() => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
    public void RequestExit()           => ExitRequested?.Invoke(this, EventArgs.Empty);
    public void Dispose()               { }
}
