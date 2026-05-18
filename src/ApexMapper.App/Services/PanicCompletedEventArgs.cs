namespace ApexMapper.App.Services;

public sealed class PanicCompletedEventArgs(string disabledExecutablePath, Exception? error) : EventArgs
{
    public string DisabledExecutablePath { get; } = disabledExecutablePath;
    public Exception? Error { get; } = error;
}
