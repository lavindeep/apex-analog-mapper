using System;
using System.IO;
using ApexMapper.App.Services;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Services;

public sealed class SupervisorProcessLauncherTests
{
    [Fact]
    public void EnsureRunning_reports_a_missing_executable_loudly()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}", "ApexMapper.Supervisor.exe");
        var launcher = new SupervisorProcessLauncher("42", missingPath);

        var error = launcher.EnsureRunning();

        error.Should().NotBeNull("a missing supervisor means no pad — never a silent no-op");
        error.Should().Contain(missingPath);
    }

    [Fact]
    public void Constructor_rejects_an_empty_session_id()
    {
        var act = () => new SupervisorProcessLauncher(string.Empty);
        act.Should().Throw<ArgumentException>();
    }
}
