namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="ILoginTaskService"/>.
/// Delegates Windows Task Scheduler operations to <see cref="ITaskSchedulerFacade"/>
/// so that the service is fully testable without a real scheduler.
/// </summary>
public sealed class LoginTaskService : ILoginTaskService
{
    private readonly ITaskSchedulerFacade _scheduler;
    private readonly LoginTaskOptions _options;

    public LoginTaskService(ITaskSchedulerFacade scheduler, LoginTaskOptions options)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(options);
        _scheduler = scheduler;
        _options = options;
    }

    /// <inheritdoc/>
    public bool IsEnabled() => _scheduler.TaskExists(_options.TaskName);

    /// <inheritdoc/>
    public void Enable()
    {
        // Idempotent: if the task already exists, delete and re-register so
        // that the executable path / description stay in sync.
        if (_scheduler.TaskExists(_options.TaskName))
            _scheduler.UnregisterTask(_options.TaskName);

        _scheduler.RegisterLogonTask(_options.TaskName, _options.ExecutablePath, _options.Description);
    }

    /// <inheritdoc/>
    public void Disable()
    {
        if (_scheduler.TaskExists(_options.TaskName))
            _scheduler.UnregisterTask(_options.TaskName);
    }
}
