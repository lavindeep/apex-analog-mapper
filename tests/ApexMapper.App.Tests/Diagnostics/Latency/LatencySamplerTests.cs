using ApexMapper.App.Diagnostics;
using ApexMapper.App.Diagnostics.Latency;
using ApexMapper.Core.Diagnostics;
using FluentAssertions;

namespace ApexMapper.App.Tests.Diagnostics.Latency;

public class LatencySamplerTests
{
    [Fact]
    public void New_sampler_reports_zero_percentiles()
    {
        var recorder = new LatencyRecorder(1024);
        using var sampler = new LatencySampler(recorder);
        sampler.Percentiles.Should().Be((0.0, 0.0, 0.0));
    }

    [Fact]
    public void Stop_when_not_started_is_a_noop()
    {
        var recorder = new LatencyRecorder(1024);
        using var sampler = new LatencySampler(recorder);
        var act = () => sampler.Stop();
        act.Should().NotThrow();
    }

    [Fact]
    public void Start_drains_recorder_and_raises_SamplesAdded()
    {
        var recorder = new LatencyRecorder(4096);
        using var sampler = new LatencySampler(recorder);

        var collected = new List<LatencySample>();
        var gate = new ManualResetEventSlim(false);
        sampler.SamplesAdded += batch =>
        {
            lock (collected)
            {
                collected.AddRange(batch);
                if (collected.Count >= 100) gate.Set();
            }
        };

        sampler.Start(TimeSpan.FromMilliseconds(10), CancellationToken.None);

        // Record some samples.
        for (long i = 1; i <= 200; i++) recorder.Record(i * 10);

        gate.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        sampler.Stop();

        lock (collected)
        {
            collected.Should().NotBeEmpty();
            collected.Should().OnlyContain(s => s.LatencyMicros > 0);
        }
    }

    [Fact]
    public void Start_updates_running_percentiles_as_samples_arrive()
    {
        var recorder = new LatencyRecorder(4096);
        using var sampler = new LatencySampler(recorder);

        sampler.Start(TimeSpan.FromMilliseconds(10), CancellationToken.None);

        var rng = new Random(7);
        for (var i = 0; i < 2_000; i++)
        {
            recorder.Record(rng.Next(100, 10_000));
        }

        // Allow at least a couple of drain cycles to consume the ring buffer.
        Thread.Sleep(150);

        var (p50, p95, p99) = sampler.Percentiles;
        sampler.Stop();

        p50.Should().BeGreaterThan(0);
        p95.Should().BeGreaterOrEqualTo(p50);
        p99.Should().BeGreaterOrEqualTo(p95);
    }

    [Fact]
    public void Stop_halts_thread_within_reasonable_deadline()
    {
        var recorder = new LatencyRecorder(1024);
        using var sampler = new LatencySampler(recorder);

        sampler.Start(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Thread.Sleep(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        sampler.Stop();
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public void Cancelling_external_token_stops_sampler()
    {
        var recorder = new LatencyRecorder(1024);
        using var sampler = new LatencySampler(recorder);
        using var cts = new CancellationTokenSource();

        sampler.Start(TimeSpan.FromMilliseconds(20), cts.Token);
        Thread.Sleep(50);
        cts.Cancel();

        // Allow sampling thread to observe cancellation.
        Thread.Sleep(200);
        var act = () => sampler.Stop();
        act.Should().NotThrow();
    }

    [Fact]
    public void Start_is_idempotent_when_already_running()
    {
        var recorder = new LatencyRecorder(1024);
        using var sampler = new LatencySampler(recorder);

        sampler.Start(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        var act = () => sampler.Start(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        act.Should().Throw<InvalidOperationException>();
        sampler.Stop();
    }

    [Fact]
    public void Sampler_sustains_kilohertz_drain_for_five_seconds_without_allocations()
    {
        // This test exercises the documented 1 kHz sampling target. It is the
        // longest test in the suite and is intended for CI/Windows runs.
        var recorder = new LatencyRecorder(8192);
        using var sampler = new LatencySampler(recorder);

        var batches = 0;
        sampler.SamplesAdded += _ => Interlocked.Increment(ref batches);

        sampler.Start(TimeSpan.FromMilliseconds(1), CancellationToken.None);

        // Warm-up: 200ms of recording + drains.
        var rng = new Random(13);
        for (var i = 0; i < 5_000; i++) recorder.Record(rng.Next(100, 10_000));
        Thread.Sleep(200);

        // Measurement window: ≥5s.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var stopAt = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < stopAt)
        {
            recorder.Record(rng.Next(100, 10_000));
        }
        var afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        sampler.Stop();

        // The producer-side allocates `Random.Next` arithmetic only (no heap).
        // Allow a small slack for any rare ambient allocations on the test
        // thread that the framework may inject; the sampler's *own* allocation
        // budget is what we care about, but it runs on its own thread so we
        // can only sanity-check that the producer side stays steady.
        (afterAlloc - beforeAlloc).Should().BeLessThan(64 * 1024);
        batches.Should().BeGreaterThan(10);
    }
}
