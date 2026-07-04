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
    public void Null_recorder_IsActive_is_false()
    {
        LatencyRecorder.Null.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Real_recorder_IsActive_is_true()
    {
        var recorder = new LatencyRecorder(16);
        recorder.IsActive.Should().BeTrue();
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

        var expected = new HashSet<long>();
        for (var t = 0; t < threadCount; t++)
        {
            for (var i = 0; i < perThread; i++)
            {
                expected.Add((long)t * 10_000 + i);
            }
        }

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

        var actual = new HashSet<long>(buffer.Take(count));
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void TrySnapshot_reports_write_count_observed_at_snapshot_time()
    {
        var recorder = new LatencyRecorder(16);
        for (var i = 0; i < 5; i++)
        {
            recorder.Record(i);
        }

        var buffer = new long[16];
        var count = recorder.TrySnapshot(buffer, out var observed);

        count.Should().Be(5);
        observed.Should().Be(5);
        observed.Should().Be(recorder.WriteCount);
    }

    [Fact]
    public void TrySnapshot_observed_count_tracks_writes_past_capacity()
    {
        var recorder = new LatencyRecorder(8);
        for (var i = 0; i < 20; i++)
        {
            recorder.Record(i);
        }

        var count = recorder.TrySnapshot(new long[8], out var observed);

        count.Should().Be(8);
        observed.Should().Be(20);
    }

    [Fact]
    public void TrySnapshot_null_recorder_reports_zero_observed_count()
    {
        var count = LatencyRecorder.Null.TrySnapshot(new long[4], out var observed);

        count.Should().Be(0);
        observed.Should().Be(0);
    }
}
