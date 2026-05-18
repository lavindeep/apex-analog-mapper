namespace ApexMapper.App.Services;

/// <summary>
/// Internal extension of ITrayService so TrayMenuViewModel can trigger ExitRequested
/// and OpenMainWindowRequested without public Request*() methods on ITrayService itself.
/// </summary>
internal interface ITrayServiceInternal : ITrayService
{
    void RequestOpenMainWindow();
    void RequestExit();
}
