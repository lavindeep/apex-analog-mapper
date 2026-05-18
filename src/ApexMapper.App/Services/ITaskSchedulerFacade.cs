namespace ApexMapper.App.Services;

/// <summary>
/// Abstraction over the Windows Task Scheduler so that <see cref="LoginTaskService"/>
/// can be tested without a real scheduler.
/// </summary>
public interface ITaskSchedulerFacade
{
    bool TaskExists(string taskName);
    void RegisterLogonTask(string taskName, string executablePath, string description);
    void UnregisterTask(string taskName);
}
