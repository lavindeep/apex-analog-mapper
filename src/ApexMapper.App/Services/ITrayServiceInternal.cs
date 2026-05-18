namespace ApexMapper.App.Services;

/// <summary>
/// Internal extension of ITrayService so TrayMenuViewModel can trigger ExitRequested
/// without a public RequestExit() method on ITrayService itself.
/// </summary>
internal interface ITrayServiceInternal
{
    void RequestExit();
}
