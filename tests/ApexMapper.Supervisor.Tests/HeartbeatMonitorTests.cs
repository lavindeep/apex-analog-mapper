using FluentAssertions;
using Xunit;

namespace ApexMapper.Supervisor.Tests;

public class HeartbeatMonitorTests
{
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(1000);

    private static (HeartbeatMonitor Monitor, ManualTimeProvider Time, Func<int> GapCount) CreateStarted()
    {
        var time = new ManualTimeProvider();
        var monitor = new HeartbeatMonitor(time, Gap);
        var count = 0;
        monitor.GapDetected += () => Interlocked.Increment(ref count);
        monitor.Start();
        return (monitor, time, () => Volatile.Read(ref count));
    }

    [Fact]
    public void No_gap_while_frames_keep_arriving_at_heartbeat_cadence()
    {
        var (monitor, time, gapCount) = CreateStarted();
        using (monitor)
        {
            for (var i = 0; i < 8; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(250));
                monitor.NotifyAlive();
            }

            gapCount().Should().Be(0);
        }
    }

    [Fact]
    public void Gap_does_not_fire_just_short_of_the_threshold()
    {
        var (monitor, time, gapCount) = CreateStarted();
        using (monitor)
        {
            time.Advance(TimeSpan.FromMilliseconds(999));

            gapCount().Should().Be(0);
        }
    }

    [Fact]
    public void Gap_fires_at_the_silence_threshold()
    {
        var (monitor, time, gapCount) = CreateStarted();
        using (monitor)
        {
            time.Advance(TimeSpan.FromMilliseconds(1000));

            gapCount().Should().Be(1);
        }
    }

    [Fact]
    public void NotifyAlive_postpones_the_gap()
    {
        var (monitor, time, gapCount) = CreateStarted();
        using (monitor)
        {
            time.Advance(TimeSpan.FromMilliseconds(600));
            monitor.NotifyAlive();

            // 1.2 s since start, but only 600 ms of silence: no gap yet. This
            // crosses the initial due time, so it exercises the re-arm path.
            time.Advance(TimeSpan.FromMilliseconds(600));
            gapCount().Should().Be(0);

            time.Advance(TimeSpan.FromMilliseconds(400));
            gapCount().Should().Be(1);
        }
    }

    [Fact]
    public void Gap_fires_exactly_once_per_monitor_lifetime()
    {
        var (monitor, time, gapCount) = CreateStarted();
        using (monitor)
        {
            time.Advance(TimeSpan.FromSeconds(5));

            gapCount().Should().Be(1);
        }
    }

    [Fact]
    public void NotifyAlive_after_the_gap_does_not_resurrect_the_monitor()
    {
        var (monitor, time, gapCount) = CreateStarted();
        using (monitor)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            gapCount().Should().Be(1);

            monitor.NotifyAlive();
            time.Advance(TimeSpan.FromSeconds(2));

            gapCount().Should().Be(1);
        }
    }

    [Fact]
    public void Dispose_stops_detection()
    {
        var (monitor, time, gapCount) = CreateStarted();
        monitor.Dispose();

        time.Advance(TimeSpan.FromSeconds(5));

        gapCount().Should().Be(0);
    }

    [Fact]
    public void Gap_handler_may_dispose_the_monitor_without_deadlock_or_throw()
    {
        var time = new ManualTimeProvider();
        var monitor = new HeartbeatMonitor(time, Gap);
        var fired = 0;
        monitor.GapDetected += () =>
        {
            Interlocked.Increment(ref fired);
            monitor.Dispose();
        };
        monitor.Start();

        var act = () => time.Advance(TimeSpan.FromSeconds(2));

        act.Should().NotThrow();
        Volatile.Read(ref fired).Should().Be(1);
    }

    [Fact]
    public void Start_twice_throws()
    {
        using var monitor = new HeartbeatMonitor(new ManualTimeProvider(), Gap);
        monitor.Start();

        var act = () => monitor.Start();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Nonpositive_gap_is_rejected()
    {
        var act = () => new HeartbeatMonitor(new ManualTimeProvider(), TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
