namespace ApexMapper.Output.Preflight;

/// <summary>
/// Runs a fixed, ordered set of pre-flight checks and aggregates their results
/// into a single <see cref="PreflightReport"/>. This is the fail-closed gate the
/// session consults before enabling output: any <see cref="PreflightSeverity.Fail"/>
/// issue sets <see cref="PreflightReport.HasBlocker"/>.
///
/// A check that THROWS is treated as a blocker, not as an absence of problems:
/// its exception is recorded as a Fail issue under its own <see cref="IPreflightCheck.CheckId"/>.
/// A crashing check must never silently vanish from the report.
/// </summary>
public sealed class PreflightRunner
{
    private readonly IReadOnlyList<IPreflightCheck> _checks;

    public PreflightRunner(IReadOnlyList<IPreflightCheck> checks) =>
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));

    public PreflightReport RunAll(TimeProvider? time = null)
    {
        var clock = time ?? TimeProvider.System;
        var issues = new List<PreflightIssue>();

        foreach (var check in _checks)
        {
            PreflightIssue? issue;
            try
            {
                issue = check.Run();
            }
            catch (Exception ex)
            {
                issue = new PreflightIssue(check.CheckId, PreflightSeverity.Fail, ex.Message, null);
            }

            if (issue is not null)
            {
                issues.Add(issue);
            }
        }

        return new PreflightReport(issues, clock.GetUtcNow());
    }
}
