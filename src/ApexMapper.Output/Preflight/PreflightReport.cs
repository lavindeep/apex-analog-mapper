namespace ApexMapper.Output.Preflight;

public enum PreflightSeverity { Pass, Warn, Fail }

public record PreflightIssue(string CheckId, PreflightSeverity Severity, string Message, string? Remediation);

public record PreflightReport(IReadOnlyList<PreflightIssue> Issues, DateTimeOffset At)
{
    public bool HasBlocker => Issues.Any(i => i.Severity == PreflightSeverity.Fail);
}
