using System.Security.Principal;
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

        // Run at the current user's logon only. A bare LogonTrigger fires for ANY
        // user's logon; scope it to the installing user via UserId.
        td.Triggers.Add(new LogonTrigger { UserId = WindowsIdentity.GetCurrent().Name });

        // Execute the application.
        td.Actions.Add(new ExecAction(executablePath));

        // Allow the task to run even if no batteries / on AC — keyboard app.
        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries     = false;
        td.Settings.ExecutionTimeLimit         = TimeSpan.Zero; // no limit

        // Register with the interactive-token logon type: a tray app needs the
        // current interactive session (the default S4U logon has no desktop/UI
        // access). CreateOrUpdate keeps registration idempotent.
        ts.RootFolder.RegisterTaskDefinition(
            taskName,
            td,
            TaskCreation.CreateOrUpdate,
            userId:    null,
            password:  null,
            logonType: TaskLogonType.InteractiveToken);
    }

    public void UnregisterTask(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        using var ts = new TaskService();
        if (ts.GetTask(taskName) is not null)
            ts.RootFolder.DeleteTask(taskName, exceptionOnNotExists: false);
    }
}
