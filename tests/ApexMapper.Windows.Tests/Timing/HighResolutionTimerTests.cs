using System.Diagnostics;
using ApexMapper.Windows.Timing;
using Xunit;

namespace ApexMapper.Windows.Tests.Timing;

public class HighResolutionTimerTests
{
    [Fact]
    public async Task Wait_returns_true_per_period_and_false_once_stopped()
    {
        using var fast = new HighResolutionTimer(1);
        Assert.True(fast.WaitNext());
        Assert.True(fast.WaitNext());

        using var timer = new HighResolutionTimer(500);
        var waiter = Task.Run(timer.WaitNext);
        Thread.Sleep(20);
        var clock = Stopwatch.StartNew();
        timer.Stop();
        var result = await waiter.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.False(result);
        Assert.False(timer.WaitNext());
        fast.Stop();
        Assert.False(fast.WaitNext());
        Assert.True(clock.Elapsed.TotalMilliseconds < 200, $"Stop took {clock.Elapsed.TotalMilliseconds:F1} ms; the period was 500 ms, so the stop event released the wait.");
    }

    [Fact]
    public void A_period_under_one_millisecond_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(0));
    }

    [HardwareFact]
    public void Period_p99_is_under_the_threshold()
    {
        using var timer = new HighResolutionTimer(1);
        var periods = new List<double>(4000);
        var last = Stopwatch.GetTimestamp();
        timer.WaitNext();
        last = Stopwatch.GetTimestamp();
        for (var i = 0; i < 3000; i++)
        {
            timer.WaitNext();
            var now = Stopwatch.GetTimestamp();
            periods.Add((now - last) * 1000d / Stopwatch.Frequency);
            last = now;
        }
        periods.Sort();

        var p99 = periods[(int)(0.99 * (periods.Count - 1))];
        Assert.True(p99 < HardwareThresholds.TimerPeriodP99Ms, $"p99 {p99:F2} ms, max {periods[^1]:F2} ms.");
    }
}
