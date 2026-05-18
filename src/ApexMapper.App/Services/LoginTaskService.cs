namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="ILoginTaskService"/>.
/// Delegates Windows Task Scheduler operations to <see cref="ITaskSchedulerFacade"/>
/// so that the service is fully testable without a real scheduler.
/// </summary>
public sealed class LoginTaskService : ILoginTaskService
{
    private readonly ITaskSchedulerFacade _scheduler;

    public LoginTaskService(ITaskSchedulerFacade scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _scheduler = scheduler;
    }

    /// <inheritdoc/>
    public bool IsEnabled() => _scheduler.TaskExists(GetDefaultTaskName());

    /// <inheritdoc/>
    public void Enable(LoginTaskOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Idempotent: if the task already exists, delete and re-register so
        // that the executable path / description stay in sync.
        if (_scheduler.TaskExists(options.TaskName))
            _scheduler.UnregisterTask(options.TaskName);

        _scheduler.RegisterLogonTask(options.TaskName, options.ExecutablePath, options.Description);
    }

    /// <inheritdoc/>
    public void Disable()
    {
        var taskName = GetDefaultTaskName();
        if (_scheduler.TaskExists(taskName))
            _scheduler.UnregisterTask(taskName);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    // The parameterless overloads (IsEnabled / Disable) use a well-known name.
    // The Enable overload accepts the name from LoginTaskOptions so tests and
    // composition root can supply any name.
    private static string GetDefaultTaskName() => "ApexProAnalogMapper";
}
