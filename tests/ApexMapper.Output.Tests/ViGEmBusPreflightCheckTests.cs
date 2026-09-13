using ApexMapper.Output.Preflight;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Output.Tests;

public class ViGEmBusPreflightCheckTests
{
    [Fact]
    public void Check_id_is_vigem_bus()
    {
        new ViGEmBusPreflightCheck(probe: () => null).CheckId.Should().Be("vigem-bus");
    }

    [Fact]
    public void Probe_success_yields_no_issue()
    {
        var check = new ViGEmBusPreflightCheck(probe: () => null);

        check.Run().Should().BeNull();
    }

    [Fact]
    public void Probe_failure_yields_a_fail_issue_with_remediation()
    {
        var check = new ViGEmBusPreflightCheck(probe: () => "ViGEmBus driver not found.");

        var issue = check.Run();

        issue.Should().NotBeNull();
        issue!.CheckId.Should().Be("vigem-bus");
        issue.Severity.Should().Be(PreflightSeverity.Fail);
        issue.Message.Should().Contain("ViGEmBus");
        issue.Remediation.Should().Contain("ViGEmBus");
    }

    [DriverlessFact]
    public void Real_default_probe_on_a_driverless_machine_reports_a_vigembus_failure()
    {
        var check = new ViGEmBusPreflightCheck();

        var issue = check.Run();

        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(PreflightSeverity.Fail);
        issue.Message.Should().Contain("ViGEmBus");
    }
}
