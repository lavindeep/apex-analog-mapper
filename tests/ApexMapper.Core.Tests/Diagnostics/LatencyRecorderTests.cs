using ApexMapper.Core.Diagnostics;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Diagnostics;

public class LatencyRecorderTests
{
    [Fact]
    public void Null_recorder_accepts_record_without_throwing()
    {
        var recorder = LatencyRecorder.Null;
        var act = () => recorder.Record(123);
        act.Should().NotThrow();
    }

    [Fact]
    public void Null_recorder_TrySnapshot_returns_zero()
    {
        var recorder = LatencyRecorder.Null;
        Span<long> buffer = stackalloc long[16];
        var count = recorder.TrySnapshot(buffer);
        count.Should().Be(0);
    }

    [Fact]
    public void Constructor_rejects_non_power_of_two_capacity()
    {
        var act = () => new LatencyRecorder(1000);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_negative_capacity()
    {
        var act = () => new LatencyRecorder(-1);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_allows_zero_capacity_for_null_mode()
    {
        var act = () => new LatencyRecorder(0);
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_allows_power_of_two_capacity()
    {
        var act = () => new LatencyRecorder(4096);
        act.Should().NotThrow();
    }

    [Fact]
    public void Records_below_capacity_are_all_readable()
    {
        var recorder = new LatencyRecorder(16);
        for (long i = 1; i <= 5; i++)
        {
            recorder.Record(i * 10);
        }

        Span<long> buffer = stackalloc long[16];
        var count = recorder.TrySnapshot(buffer);
        count.Should().Be(5);
        buffer[..5].ToArray().Should().BeEquivalentTo(new long[] { 10, 20, 30, 40, 50 }, opt => opt.WithStrictOrdering());
    }

    [Fact]
    public void Snapshot_after_wrap_returns_capacity_most_recent_samples()
    {
        var recorder = new LatencyRecorder(8);
        for (long i = 1; i <= 12; i++)
        {
            recorder.Record(i);
        }

        Span<long> buffer = stackalloc long[8];
        var count = recorder.TrySnapshot(buffer);
        count.Should().Be(8);
        buffer.ToArray().Should().BeEquivalentTo(new long[] { 5, 6, 7, 8, 9, 10, 11, 12 }, opt => opt.WithStrictOrdering());
    }

    [Fact]
    public void Snapshot_into_smaller_buffer_copies_most_recent_samples()
    {
        var recorder = new LatencyRecorder(16);
        for (long i = 1; i <= 10; i++)
        {
            recorder.Record(i);
        }

        Span<long> buffer = stackalloc long[4];
        var count = recorder.TrySnapshot(buffer);
        count.Should().Be(4);
        buffer.ToArray().Should().BeEquivalentTo(new long[] { 7, 8, 9, 10 }, opt => opt.WithStrictOrdering());
    }

    [Fact]
    public void Concurrent_records_within_capacity_lose_no_samples()
    {
        const int threadCount = 4;
        const int perThread = 256;
        var recorder = new LatencyRecorder(4096);
        var threads = new Thread[threadCount];
        var ready = new ManualResetEventSlim(false);

        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                ready.Wait();
                for (var i = 0; i < perThread; i++)
                {
                    recorder.Record((long)threadIndex * 10_000 + i);
                }
            });
            threads[t].Start();
        }

        ready.Set();
        foreach (var thread in threads) thread.Join();

        var buffer = new long[4096];
        var count = recorder.TrySnapshot(buffer);
        count.Should().Be(threadCount * perThread);
    }
}
