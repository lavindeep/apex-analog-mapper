namespace ApexMapper.App.Services;

/// <summary>Manages a Windows Task Scheduler entry that launches the app at user login.</summary>
public interface ILoginTaskService
{
    bool IsEnabled();
    void Enable();
    void Disable();
}

public sealed record LoginTaskOptions(
    string ExecutablePath,
    string TaskName,
    string Description);
