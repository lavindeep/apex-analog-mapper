using System.Diagnostics;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.Windows.Tests.Session;

public class GameProcessTests
{
    private static Process StartChild() => Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
    {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
    })!;

    [Fact]
    public void A_running_process_is_found_by_its_path_and_a_missing_path_is_not()
    {
        using var game = GameProcess.Find(Environment.ProcessPath!);

        Assert.NotNull(game);
        Assert.Null(GameProcess.Find(@"C:\Nowhere\NotAGame.exe"));
    }

    [Fact]
    public void Exit_is_reported_once_the_last_process_ends()
    {
        using var first = StartChild();
        using var second = StartChild();
        using var game = GameProcess.Watch([(uint)first.Id, (uint)second.Id])!;
        using var exited = new ManualResetEventSlim(false);
        game.OnExit(exited.Set);

        first.Kill();
        first.WaitForExit(2000);
        Assert.False(exited.Wait(200, TestContext.Current.CancellationToken), "one of two processes is still running");

        second.Kill();
        Assert.True(exited.Wait(2000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_game_that_already_exited_reports_at_once_and_a_disposed_watch_reports_nothing()
    {
        using var child = StartChild();
        var game = GameProcess.Watch([(uint)child.Id])!;
        using var exited = new ManualResetEventSlim(false);
        child.Kill();
        child.WaitForExit(2000);

        game.OnExit(exited.Set);
        Assert.True(exited.Wait(2000, TestContext.Current.CancellationToken));
        game.Dispose();

        using var other = StartChild();
        using var watch = GameProcess.Watch([(uint)other.Id])!;
        using var late = new ManualResetEventSlim(false);
        watch.OnExit(late.Set);
        watch.Dispose();
        other.Kill();
        Assert.False(late.Wait(300, TestContext.Current.CancellationToken));
    }
}
