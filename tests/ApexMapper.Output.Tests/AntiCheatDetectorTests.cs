using ApexMapper.Output.Detection;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Output.Tests;

public class AntiCheatDetectorTests
{
    private static ForegroundContext Foreground(string? exePath = null) =>
        new(ProcessId: 100, ExecutablePath: exePath, WindowTitle: null, SteamAppId: null, CapturedAt: DateTimeOffset.UnixEpoch);

    private static ProcessSnapshot Proc(int pid, string name, string? path = null) =>
        new(pid, ParentProcessId: 0, name, path, EnvironmentVariables: new Dictionary<string, string>());

    private sealed class FakeEnumerator : IProcessEnumerator
    {
        private readonly IReadOnlyList<ProcessSnapshot> _processes;
        public FakeEnumerator(params ProcessSnapshot[] processes) => _processes = processes;
        public IReadOnlyList<ProcessSnapshot> Enumerate() => _processes;
        public ProcessSnapshot? GetById(int processId) => _processes.FirstOrDefault(p => p.ProcessId == processId);
    }

    private sealed class ThrowingEnumerator : IProcessEnumerator
    {
        public IReadOnlyList<ProcessSnapshot> Enumerate() => throw new InvalidOperationException("snapshot failed");
        public ProcessSnapshot? GetById(int processId) => null;
    }

    private sealed class NullEnumerator : IProcessEnumerator
    {
        public IReadOnlyList<ProcessSnapshot> Enumerate() => null!;
        public ProcessSnapshot? GetById(int processId) => null;
    }

    [Theory]
    [InlineData("BEService.exe")]
    [InlineData("EasyAntiCheat.exe")]
    [InlineData("vgc.exe")]
    [InlineData("FACEITService.exe")]
    public void Detects_each_builtin_service_exact_case(string serviceName)
    {
        var detector = new AntiCheatDetector(new FakeEnumerator(Proc(1, "explorer.exe"), Proc(2, serviceName)));

        var verdict = detector.Evaluate(Foreground());

        verdict.Action.Should().Be(AntiCheatAction.DisableAutoEnable);
        verdict.MatchedSignal.Should().Be(serviceName);
    }

    [Theory]
    [InlineData("beservice.exe")]
    [InlineData("EASYANTICHEAT.EXE")]
    [InlineData("Vgc.exe")]
    public void Detects_service_different_case(string serviceName)
    {
        var detector = new AntiCheatDetector(new FakeEnumerator(Proc(2, serviceName)));

        var verdict = detector.Evaluate(Foreground());

        verdict.Action.Should().Be(AntiCheatAction.DisableAutoEnable);
        verdict.MatchedSignal.Should().Be(serviceName);
    }

    [Theory]
    [InlineData("BEService")]
    [InlineData("vgc")]
    [InlineData("EasyAntiCheat")]
    public void Detects_service_without_exe_suffix(string serviceName)
    {
        var detector = new AntiCheatDetector(new FakeEnumerator(Proc(2, serviceName)));

        var verdict = detector.Evaluate(Foreground());

        verdict.Action.Should().Be(AntiCheatAction.DisableAutoEnable);
        verdict.MatchedSignal.Should().Be(serviceName);
    }

    [Fact]
    public void Benign_process_list_allows()
    {
        var detector = new AntiCheatDetector(new FakeEnumerator(Proc(1, "explorer.exe"), Proc(2, "chrome.exe"), Proc(3, "game.exe")));

        var verdict = detector.Evaluate(Foreground());

        verdict.Action.Should().Be(AntiCheatAction.Allow);
        verdict.MatchedSignal.Should().BeNull();
    }

    [Fact]
    public void Extra_service_names_are_honored()
    {
        var detector = new AntiCheatDetector(
            new FakeEnumerator(Proc(2, "customac.exe")),
            extraServiceNames: new[] { "customac.exe" });

        var verdict = detector.Evaluate(Foreground());

        verdict.Action.Should().Be(AntiCheatAction.DisableAutoEnable);
    }

    [Theory]
    [InlineData(@"C:\Games\Blocked\blocked.exe")]
    [InlineData("c:/games/blocked/BLOCKED.EXE")]
    public void Detects_blocked_foreground_executable_by_file_name(string path)
    {
        var detector = new AntiCheatDetector(
            new FakeEnumerator(Proc(1, "explorer.exe")),
            blockedExecutables: new[] { "blocked.exe" });

        var verdict = detector.Evaluate(Foreground(path));

        verdict.Action.Should().Be(AntiCheatAction.DisableAutoEnable);
        verdict.MatchedSignal.Should().BeEquivalentTo("blocked.exe");
    }

    [Fact]
    public void Blocked_executables_default_empty_allows_any_foreground()
    {
        var detector = new AntiCheatDetector(new FakeEnumerator(Proc(1, "explorer.exe")));

        var verdict = detector.Evaluate(Foreground(@"C:\Games\anything\anything.exe"));

        verdict.Action.Should().Be(AntiCheatAction.Allow);
    }

    [Fact]
    public void Enumerator_that_throws_fails_closed()
    {
        var detector = new AntiCheatDetector(new ThrowingEnumerator());

        var verdict = detector.Evaluate(Foreground());

        verdict.Action.Should().Be(AntiCheatAction.DisableAutoEnable);
        verdict.Reason.Should().Contain("unavailable");
    }

    [Fact]
    public void Enumerator_that_returns_null_fails_closed()
    {
        var detector = new AntiCheatDetector(new NullEnumerator());

        var verdict = detector.Evaluate(Foreground());

        verdict.Action.Should().Be(AntiCheatAction.DisableAutoEnable);
        verdict.Reason.Should().Contain("unavailable");
    }

    [Fact]
    public void Null_foreground_with_clean_scan_allows()
    {
        var detector = new AntiCheatDetector(new FakeEnumerator(Proc(1, "explorer.exe")));

        var verdict = detector.Evaluate(foreground: null);

        verdict.Action.Should().Be(AntiCheatAction.Allow);
    }
}
