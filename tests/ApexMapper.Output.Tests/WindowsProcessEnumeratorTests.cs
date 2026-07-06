using System.Diagnostics;
using ApexMapper.Output.Detection;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Output.Tests;

public class WindowsProcessEnumeratorTests
{
    [Fact]
    public void Construction_is_safe_off_windows()
    {
        // The type is Windows-only at runtime but must construct anywhere so
        // composition roots can be assembled and tested on the macOS gate.
        var act = () => new WindowsProcessEnumerator();

        act.Should().NotThrow();
    }

    [Fact]
    public void Enumerate_off_windows_throws_platform_not_supported()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The negative-platform contract is only observable off Windows.
        }

        var enumerator = new WindowsProcessEnumerator();

        var act = () => enumerator.Enumerate();

        act.Should().Throw<PlatformNotSupportedException>();
    }

    [Fact]
    public void Enumerate_contains_the_current_process_with_a_nonzero_parent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Toolhelp32 only exists on Windows.
        }

        var enumerator = new WindowsProcessEnumerator();
        var currentPid = Environment.ProcessId;

        var processes = enumerator.Enumerate();

        var self = processes.Should().ContainSingle(p => p.ProcessId == currentPid).Subject;
        self.ParentProcessId.Should().NotBe(0);
        self.Name.Should().NotBeNullOrEmpty();
        self.EnvironmentVariables.Should().BeEmpty();
    }

    [Fact]
    public void GetById_round_trips_the_current_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var enumerator = new WindowsProcessEnumerator();
        var currentPid = Environment.ProcessId;

        var snapshot = enumerator.GetById(currentPid);

        snapshot.Should().NotBeNull();
        snapshot!.ProcessId.Should().Be(currentPid);
        snapshot.EnvironmentVariables.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999999999)]
    public void GetById_returns_null_for_unknown_pid(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var enumerator = new WindowsProcessEnumerator();

        enumerator.GetById(pid).Should().BeNull();
    }
}
