using Microsoft.Win32.TaskScheduler;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="ITaskSchedulerFacade"/> that delegates to the real
/// Windows Task Scheduler via <c>TaskScheduler 2.x</c> (NuGet package
/// <c>TaskScheduler</c>, namespace <c>Microsoft.Win32.TaskScheduler</c>).
/// </summary>
public sealed class WindowsTaskSchedulerFacade : ITaskSchedulerFacade
{
    public bool TaskExists(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        using var ts = new TaskService();
        return ts.GetTask(taskName) is not null;
    }

    public void RegisterLogonTask(string taskName, string executablePath, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using var ts = new TaskService();

        var td = ts.NewTask();
        td.RegistrationInfo.Description = description ?? string.Empty;

        // Run at user logon (current user only).
        td.Triggers.Add(new LogonTrigger());

        // Execute the application.
        td.Actions.Add(new ExecAction(executablePath));

        // Allow the task to run even if no batteries / on AC — keyboard app.
        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries     = false;
        td.Settings.ExecutionTimeLimit         = TimeSpan.Zero; // no limit

        ts.RootFolder.RegisterTaskDefinition(taskName, td);
    }

    public void UnregisterTask(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        using var ts = new TaskService();
        if (ts.GetTask(taskName) is not null)
            ts.RootFolder.DeleteTask(taskName, exceptionOnNotExists: false);
    }
}
