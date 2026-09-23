using System.Diagnostics;
using Xunit;

namespace ApexMapper.Windows.Tests.Session;

/// <summary>The crash guard's subscription, proven the only way it can be: a child process that really dies of an unhandled exception.</summary>
public class CrashGuardTests
{
    [Fact]
    public async Task An_unhandled_exception_on_any_thread_runs_the_emergency_before_the_process_ends()
    {
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ApexMapper.Harness.exe"), "crash-child")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        _ = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.True(child.WaitForExit(10_000), "the child did not end");

        Assert.Contains("emergency ran", await output);
        Assert.NotEqual(0, child.ExitCode);
    }
}