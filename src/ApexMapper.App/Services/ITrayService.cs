namespace ApexMapper.App.Services;

/// <summary>Controls the system-tray notify icon and its associated balloon notifications.</summary>
public interface ITrayService : IDisposable
{
    event EventHandler? OpenMainWindowRequested;
    event EventHandler? ExitRequested;

    void Show();
    void Hide();
    void SetEnabled(bool enabled);
    void SetTooltip(string text);
    void ShowBalloon(string title, string message);
}
