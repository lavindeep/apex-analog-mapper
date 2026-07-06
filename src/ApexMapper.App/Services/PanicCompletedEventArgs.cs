namespace ApexMapper.App.Services;

public sealed class PanicCompletedEventArgs(
    string disabledExecutablePath,
    Exception? error,
    Exception? policyError = null) : EventArgs
{
    public string DisabledExecutablePath { get; } = disabledExecutablePath;

    /// <summary>Failure from submitting the panic frame to the supervisor, if any.</summary>
    public Exception? Error { get; } = error;

    /// <summary>
    /// Failure from persisting the auto-enable policy, if any. The panic frame is
    /// always submitted regardless of this failure; it is surfaced for diagnostics.
    /// </summary>
    public Exception? PolicyError { get; } = policyError;
}
