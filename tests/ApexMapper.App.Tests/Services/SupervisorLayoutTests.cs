using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Services;

/// <summary>
/// Guards the App→Supervisor project reference against silently reverting to
/// <c>ReferenceOutputAssembly="false"</c>, under which the SDK copies only the
/// supervisor apphost + .deps.json + .runtimeconfig.json and NOT
/// ApexMapper.Supervisor.dll — leaving the launcher to spawn an executable that
/// dies instantly ("The application to execute does not exist") while the UI
/// reports "enabled" and no output ever flows.
/// </summary>
public sealed class SupervisorLayoutTests
{
    [Fact]
    public void Supervisor_executable_and_assembly_are_staged_beside_the_app()
    {
        // Only a real Windows build produces the supervisor apphost (.exe) and
        // stages the copy-local closure beside the test assembly; other hosts
        // cannot observe the layout, so this proof is a Windows-CI check. It is
        // designed to FAIL against the old ReferenceOutputAssembly="false" shape,
        // under which ApexMapper.Supervisor.dll is absent.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = AppContext.BaseDirectory;

        // The load-bearing file: present ONLY with a plain ProjectReference. Its
        // absence is exactly the dead-on-arrival defect.
        File.Exists(Path.Combine(dir, "ApexMapper.Supervisor.dll"))
            .Should().BeTrue("the supervisor managed assembly must be copied beside the app");

        // The apphost the launcher actually spawns.
        File.Exists(Path.Combine(dir, "ApexMapper.Supervisor.exe"))
            .Should().BeTrue("the supervisor apphost must be spawnable beside the app");

        // Its runtime config. The supervisor's remaining dependency closure
        // (Core, Output, Logging, MessagePack) is fully shared with the App, so
        // no supervisor-unique closure dependency exists to assert — a missing
        // one could not occur without also breaking the App itself. The .dll and
        // .runtimeconfig.json are therefore the complete supervisor-specific set.
        File.Exists(Path.Combine(dir, "ApexMapper.Supervisor.runtimeconfig.json"))
            .Should().BeTrue("the supervisor runtime config must be copied beside the app");
    }
}
