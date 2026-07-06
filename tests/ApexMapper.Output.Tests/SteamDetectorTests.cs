using ApexMapper.Output.Detection;
using FluentAssertions;
using Xunit;

namespace ApexMapper.Output.Tests;

public class SteamDetectorTests
{
    private static ForegroundContext Foreground(int pid, string? exePath = null, string? appId = null) =>
        new(pid, ExecutablePath: exePath, WindowTitle: null, SteamAppId: appId, CapturedAt: DateTimeOffset.UnixEpoch);

    private static ProcessSnapshot Proc(int pid, int parentPid, string name) =>
        new(pid, parentPid, name, ExecutablePath: null, EnvironmentVariables: new Dictionary<string, string>());

    private sealed class FakeEnumerator : IProcessEnumerator
    {
        private readonly Dictionary<int, ProcessSnapshot> _byId;
        public FakeEnumerator(params ProcessSnapshot[] processes) => _byId = processes.ToDictionary(p => p.ProcessId);
        public IReadOnlyList<ProcessSnapshot> Enumerate() => _byId.Values.ToList();
        public ProcessSnapshot? GetById(int processId) => _byId.TryGetValue(processId, out var p) ? p : null;
    }

    private sealed class ThrowingEnumerator : IProcessEnumerator
    {
        public IReadOnlyList<ProcessSnapshot> Enumerate() => throw new InvalidOperationException("boom");
        public ProcessSnapshot? GetById(int processId) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Parent_chain_hit_at_depth_1()
    {
        var enumerator = new FakeEnumerator(
            Proc(100, parentPid: 50, "game.exe"),
            Proc(50, parentPid: 1, "steam.exe"));
        var detector = new SteamDetector(enumerator);

        var verdict = detector.Evaluate(Foreground(100));

        verdict.IsSteamLaunched.Should().BeTrue();
        verdict.Reason.Should().Contain("parent");
    }

    [Fact]
    public void Parent_chain_hit_at_depth_3()
    {
        var enumerator = new FakeEnumerator(
            Proc(100, 90, "game.exe"),
            Proc(90, 80, "a.exe"),
            Proc(80, 70, "b.exe"),
            Proc(70, 1, "steam.exe"));
        var detector = new SteamDetector(enumerator);

        var verdict = detector.Evaluate(Foreground(100));

        verdict.IsSteamLaunched.Should().BeTrue();
    }

    [Fact]
    public void Self_parenting_cycle_terminates()
    {
        var enumerator = new FakeEnumerator(Proc(100, parentPid: 100, "game.exe"));
        var detector = new SteamDetector(enumerator);

        var verdict = detector.Evaluate(Foreground(100));

        verdict.IsSteamLaunched.Should().BeFalse();
    }

    [Fact]
    public void Two_node_cycle_terminates()
    {
        var enumerator = new FakeEnumerator(
            Proc(100, 200, "game.exe"),
            Proc(200, 100, "other.exe"));
        var detector = new SteamDetector(enumerator);

        var verdict = detector.Evaluate(Foreground(100));

        verdict.IsSteamLaunched.Should().BeFalse();
    }

    [Fact]
    public void Depth_bound_is_respected()
    {
        // A chain of 40 non-steam ancestors with steam only beyond the bound
        // must not be detected (and must terminate).
        var procs = new List<ProcessSnapshot>();
        for (var i = 0; i < 40; i++)
        {
            procs.Add(Proc(100 + i, parentPid: 100 + i + 1, "link.exe"));
        }
        procs.Add(Proc(140, parentPid: 1, "steam.exe"));
        var detector = new SteamDetector(new FakeEnumerator(procs.ToArray()));

        var verdict = detector.Evaluate(Foreground(100));

        verdict.IsSteamLaunched.Should().BeFalse();
    }

    [Fact]
    public void Missing_parent_ends_walk()
    {
        var enumerator = new FakeEnumerator(Proc(100, parentPid: 999, "game.exe"));
        var detector = new SteamDetector(enumerator);

        var verdict = detector.Evaluate(Foreground(100));

        verdict.IsSteamLaunched.Should().BeFalse();
    }

    [Fact]
    public void App_id_signal_is_positive_and_propagates_id()
    {
        var detector = new SteamDetector(new FakeEnumerator(Proc(100, 1, "game.exe")));

        var verdict = detector.Evaluate(Foreground(100, appId: "570"));

        verdict.IsSteamLaunched.Should().BeTrue();
        verdict.SteamAppId.Should().Be("570");
        verdict.Reason.Should().Contain("app");
    }

    [Theory]
    [InlineData(@"C:\Games\SteamLibrary\steamapps\common\X\x.exe")]
    [InlineData("c:/games/steamlibrary/steamapps/common/X/x.exe")]
    public void Library_path_hit(string exePath)
    {
        var detector = new SteamDetector(
            new FakeEnumerator(Proc(100, 1, "game.exe")),
            steamLibraryRoots: new[] { @"C:\Games\SteamLibrary" });

        var verdict = detector.Evaluate(Foreground(100, exePath));

        verdict.IsSteamLaunched.Should().BeTrue();
        verdict.Reason.Should().Contain("library");
    }

    [Fact]
    public void Library_path_near_miss_sibling_directory_is_not_a_hit()
    {
        var detector = new SteamDetector(
            new FakeEnumerator(Proc(100, 1, "game.exe")),
            steamLibraryRoots: new[] { @"C:\Games\SteamLibrary" });

        var verdict = detector.Evaluate(Foreground(100, @"C:\Games\SteamLibrary2\steamapps\x.exe"));

        verdict.IsSteamLaunched.Should().BeFalse();
    }

    [Fact]
    public void No_signals_is_negative()
    {
        var detector = new SteamDetector(new FakeEnumerator(Proc(100, 1, "game.exe")));

        var verdict = detector.Evaluate(Foreground(100, @"C:\Elsewhere\x.exe"));

        verdict.IsSteamLaunched.Should().BeFalse();
        verdict.SteamAppId.Should().BeNull();
    }

    [Fact]
    public void Enumerator_throwing_still_evaluates_path_and_appid_signals()
    {
        var detector = new SteamDetector(
            new ThrowingEnumerator(),
            steamLibraryRoots: new[] { @"C:\Games\SteamLibrary" });

        var verdict = detector.Evaluate(Foreground(100, @"C:\Games\SteamLibrary\common\x.exe"));

        verdict.IsSteamLaunched.Should().BeTrue();
    }
}
