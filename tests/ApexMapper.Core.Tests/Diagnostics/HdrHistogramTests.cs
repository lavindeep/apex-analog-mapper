using ApexMapper.Core.Diagnostics;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Diagnostics;

public class HdrHistogramTests
{
    [Fact]
    public void New_histogram_has_zero_total_count()
    {
        var histogram = new HdrHistogram();
        histogram.TotalCount.Should().Be(0);
    }

    [Fact]
    public void Record_increments_total_count()
    {
        var histogram = new HdrHistogram();
        histogram.Record(100);
        histogram.Record(200);
        histogram.TotalCount.Should().Be(2);
    }

    [Fact]
    public void Reset_zeros_total_count()
    {
        var histogram = new HdrHistogram();
        histogram.Record(100);
        histogram.Record(200);
        histogram.Reset();
        histogram.TotalCount.Should().Be(0);
    }

    [Fact]
    public void Reset_after_records_clears_percentile_state()
    {
        var histogram = new HdrHistogram();
        for (var i = 0; i < 1000; i++) histogram.Record(5000);
        histogram.Reset();
        histogram.Record(123);
        var (p50, p95, p99) = histogram.Percentiles();
        p50.Should().BeInRange(110, 140);
        p95.Should().BeInRange(110, 140);
        p99.Should().BeInRange(110, 140);
    }

    [Fact]
    public void Negative_and_zero_samples_are_clamped_to_minimum_bucket()
    {
        var histogram = new HdrHistogram();
        histogram.Record(-10);
        histogram.Record(0);
        histogram.Record(1);
        histogram.TotalCount.Should().Be(3);
    }

    [Fact]
    public void Samples_above_max_range_are_clamped_to_top_bucket()
    {
        var histogram = new HdrHistogram();
        histogram.Record(long.MaxValue);
        histogram.Record(1_000_000_000);
        histogram.TotalCount.Should().Be(2);
        var (_, _, p99) = histogram.Percentiles();
        p99.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Percentile_on_empty_histogram_returns_zero()
    {
        var histogram = new HdrHistogram();
        var (p50, p95, p99) = histogram.Percentiles();
        p50.Should().Be(0);
        p95.Should().Be(0);
        p99.Should().Be(0);
    }

    [Fact]
    public void Percentile_within_one_percent_on_million_sample_normal_distribution()
    {
        // mean 5ms, sigma 2ms, clipped to [1, 16000] microseconds
        const int n = 1_000_000;
        var rng = new Random(42);
        var raw = new long[n];
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            var x = 5000.0 + 2000.0 * z;
            if (x < 1) x = 1;
            if (x > 15999) x = 15999;
            raw[i] = (long)x;
        }

        var histogram = new HdrHistogram();
        for (var i = 0; i < n; i++) histogram.Record(raw[i]);

        var sorted = (long[])raw.Clone();
        Array.Sort(sorted);
        var refP50 = (double)sorted[(int)(n * 0.50) - 1];
        var refP95 = (double)sorted[(int)(n * 0.95) - 1];
        var refP99 = (double)sorted[(int)(n * 0.99) - 1];

        var (p50, p95, p99) = histogram.Percentiles();

        ((double)p50).Should().BeApproximately(refP50, refP50 * 0.01);
        ((double)p95).Should().BeApproximately(refP95, refP95 * 0.01);
        ((double)p99).Should().BeApproximately(refP99, refP99 * 0.01);
    }

    [Fact]
    public void Concurrent_record_from_multiple_threads_preserves_count()
    {
        var histogram = new HdrHistogram();
        const int threadCount = 4;
        const int perThread = 25_000;
        var threads = new Thread[threadCount];
        var ready = new ManualResetEventSlim(false);

        for (var t = 0; t < threadCount; t++)
        {
            var seed = t;
            threads[t] = new Thread(() =>
            {
                ready.Wait();
                var rng = new Random(seed);
                for (var i = 0; i < perThread; i++)
                {
                    histogram.Record(rng.Next(1, 10_000));
                }
            });
            threads[t].Start();
        }

        ready.Set();
        foreach (var thread in threads) thread.Join();

        histogram.TotalCount.Should().Be(threadCount * perThread);
    }

    [Fact]
    public void Sequential_record_after_warmup_allocates_no_bytes()
    {
        var histogram = new HdrHistogram();
        // Warm up: JIT and any first-call internals.
        for (var i = 0; i < 1000; i++) histogram.Record(i + 1);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            histogram.Record(i + 1);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0);
    }

    [Fact]
    public void Percentiles_after_warmup_allocates_no_bytes()
    {
        var histogram = new HdrHistogram();
        for (var i = 0; i < 1000; i++) histogram.Record(i + 1);
        // Warm up call.
        _ = histogram.Percentiles();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = histogram.Percentiles();
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0);
    }

    [Fact]
    public void Buckets_exposes_readable_view()
    {
        var histogram = new HdrHistogram();
        histogram.Record(1);
        histogram.Record(8);
        histogram.Buckets.Should().NotBeNull();
        histogram.Buckets.Sum().Should().Be(2);
    }

    [Fact]
    public void Low_octave_samples_resolve_to_correct_bucket()
    {
        // Bucket math under 16 µs used to be asymmetric: BucketIndex spread
        // values across all 16 sub-buckets of each low octave, but
        // BucketLowerBound used a 1 µs sub-width, so the lower/upper bounds
        // of a low-octave bucket no longer contained the recorded sample.
        // The fix makes BucketIndex use a per-µs sub-bucket for octaves < 4,
        // matching BucketLowerBound/UpperBound. Each input µs should now be
        // recovered (via percentiles) within ±1 µs of itself when it's the
        // sole sample in the histogram.
        for (long us = 1; us <= 15; us++)
        {
            var histogram = new HdrHistogram();
            histogram.Record(us);
            var (p50, p95, p99) = histogram.Percentiles();
            p50.Should().BeInRange(us, us + 1, $"input {us}");
            p95.Should().BeInRange(us, us + 1, $"input {us}");
            p99.Should().BeInRange(us, us + 1, $"input {us}");
        }
    }

    [Fact]
    public void Zero_sample_does_not_throw_and_is_recorded()
    {
        // Zero (and negatives) must clamp into the lowest bucket without
        // crashing or producing garbage upper bounds.
        var histogram = new HdrHistogram();
        histogram.Record(0);
        histogram.TotalCount.Should().Be(1);
        var (p50, p95, p99) = histogram.Percentiles();
        // The minimum bucket covers [1, 2) µs; interpolation lands inside.
        p50.Should().BeInRange(1, 2);
        p95.Should().BeInRange(1, 2);
        p99.Should().BeInRange(1, 2);
    }
}
