using ApexMapper.Output.ViGEm;
using Nefarius.ViGEm.Client;

namespace ApexMapper.Output.Preflight;

/// <summary>
/// Pre-flight check for the ViGEmBus driver. It probes the driver by attempting
/// a throwaway client connection: success means the bus is present and usable,
/// failure means output cannot be created and the session must be blocked with
/// an actionable message.
///
/// The probe is injectable so the pass/fail branches are testable off Windows;
/// the default probe news up a real <see cref="ViGEmClient"/> and reuses
/// <see cref="ViGEmFailure"/> for its wording so the pre-flight message matches
/// the runtime connect failure exactly.
/// </summary>
public sealed class ViGEmBusPreflightCheck : IPreflightCheck
{
    private const string Remediation =
        "Install the ViGEmBus driver (1.22.0 or newer) from " +
        "https://github.com/nefarius/ViGEmBus/releases, then re-run pre-flight.";

    private readonly Func<string?> _probe;

    /// <param name="probe">
    /// Returns <c>null</c> when the ViGEmBus driver is present and usable, or a
    /// human-readable failure description otherwise. Defaults to a real driver probe.
    /// </param>
    public ViGEmBusPreflightCheck(Func<string?>? probe = null) => _probe = probe ?? DefaultProbe;

    public string CheckId => "vigem-bus";

    public PreflightIssue? Run()
    {
        var failure = _probe();
        return failure is null
            ? null
            : new PreflightIssue(CheckId, PreflightSeverity.Fail, failure, Remediation);
    }

    private static string? DefaultProbe()
    {
        try
        {
            using var client = new ViGEmClient();
            return null;
        }
        catch (Exception ex)
        {
            return ViGEmFailure.Describe(ex);
        }
    }
}
