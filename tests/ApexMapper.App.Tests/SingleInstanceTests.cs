using Xunit;

namespace ApexMapper.App.Tests;

/// <summary>
/// Claims under names of the test's own, so a running copy of the app never sees them.
/// The mutex belongs to the thread that claimed it, so other launches run on threads of their own.
/// </summary>
public sealed class SingleInstanceTests
{
    private readonly string _name = "ApexAnalogMapper.Tests." + Guid.NewGuid().ToString("N");

    /// <summary>Another launch, which lets go of anything it claims at once.</summary>
    private Thread Launch(TimeSpan wait, Action<bool> claimed)
    {
        var thread = new Thread(() =>
        {
            using var instance = SingleInstance.TryClaim(wait, _name);
            claimed(instance is not null);
        });
        thread.Start();
        return thread;
    }

    [Fact]
    public void A_second_launch_asks_the_first_to_show_its_window_and_exits()
    {
        using var shown = new ManualResetEventSlim();
        bool? second = null;
        using (var first = SingleInstance.TryClaim(TimeSpan.Zero, _name))
        {
            Assert.NotNull(first);
            first.OnShowRequested(shown.Set);

            Launch(TimeSpan.Zero, claimed => second = claimed).Join();

            Assert.False(second);
            Assert.True(shown.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken), "the first copy was asked to show its window");
        }

        Launch(TimeSpan.Zero, claimed => second = claimed).Join();
        Assert.True(second);
    }

    [Fact]
    public void A_restart_waits_for_the_copy_that_is_closing()
    {
        var first = SingleInstance.TryClaim(TimeSpan.Zero, _name)!;
        var restarted = false;
        var restart = Launch(TimeSpan.FromSeconds(5), claimed => restarted = claimed);

        Assert.False(restart.Join(100), "the restart waits while the first copy runs");
        first.Dispose();

        Assert.True(restart.Join(TimeSpan.FromSeconds(5)));
        Assert.True(restarted);
    }
}
