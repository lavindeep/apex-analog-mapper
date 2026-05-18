using System.ComponentModel;
using ApexMapper.App.Diagnostics;
using ApexMapper.App.Diagnostics.Latency;
using FluentAssertions;

namespace ApexMapper.App.Tests.Diagnostics.Latency;

public class LatencyHistogramViewModelTests
{
    private sealed class FakeSampler : ILatencySampler
    {
        public (double P50, double P95, double P99) Percentiles { get; set; }
        public event Action<IReadOnlyList<LatencySample>>? SamplesAdded;
        public void Start(TimeSpan interval, CancellationToken ct) { }
        public void Stop() { }
        public void Emit(IReadOnlyList<LatencySample> batch) => SamplesAdded?.Invoke(batch);
    }

    [Fact]
    public void Initial_percentiles_are_zero()
    {
        var sampler = new FakeSampler();
        var vm = new LatencyHistogramViewModel(sampler);
        vm.P50.Should().Be(0);
        vm.P95.Should().Be(0);
        vm.P99.Should().Be(0);
    }

    [Fact]
    public void SamplesAdded_updates_percentiles_and_raises_PropertyChanged()
    {
        // The view-model owns an internal HdrHistogramAdapter and computes
        // P50/P95/P99 from its own bucket counts (the sampler's Percentiles
        // property is not consulted). Feed a known distribution and verify the
        // ordering invariant plus a sane range.
        var sampler = new FakeSampler();
        var vm = new LatencyHistogramViewModel(sampler);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null) changed.Add(e.PropertyName);
        };

        // 1000 samples uniformly spaced across [1000, 10000] µs.
        var batch = new List<LatencySample>(1000);
        for (var i = 0; i < 1000; i++)
        {
            // i=0 -> 1000, i=999 -> 10000
            long micros = 1000 + (i * 9000L / 999);
            batch.Add(new LatencySample(0, micros));
        }
        sampler.Emit(batch);

        // Ordering invariant — robust against bucket-edge interpolation.
        vm.P50.Should().BeLessThan(vm.P95);
        vm.P95.Should().BeLessThan(vm.P99);

        // All percentiles fall within the input range (allow a small bucket-
        // boundary slack: log buckets near 8192 µs are ~512 µs wide).
        vm.P50.Should().BeInRange(1000, 10500);
        vm.P95.Should().BeInRange(1000, 10500);
        vm.P99.Should().BeInRange(1000, 10500);

        // Distribution should put P50 in the middle of the range.
        vm.P50.Should().BeInRange(4000, 7000);

        // PropertyChanged fires for all three percentile properties because
        // they all transition from 0 to a positive value.
        changed.Should().Contain(new[]
        {
            nameof(LatencyHistogramViewModel.P50),
            nameof(LatencyHistogramViewModel.P95),
            nameof(LatencyHistogramViewModel.P99),
        });
    }

    [Fact]
    public void Single_sample_places_all_percentiles_in_same_bucket()
    {
        // A single sample at 5000 µs lands in one log-bucket of the
        // HdrHistogram (~256 µs wide at this octave). P50/P95/P99 are
        // interpolated within that bucket so they cluster tightly together.
        var sampler = new FakeSampler();
        var vm = new LatencyHistogramViewModel(sampler);

        sampler.Emit(new[] { new LatencySample(0, 5000) });

        // The bucket containing 5000 µs is [4864, 5120) — width 256 µs. All
        // three percentiles should fall inside that bucket.
        vm.P50.Should().BeInRange(4864, 5120);
        vm.P95.Should().BeInRange(4864, 5120);
        vm.P99.Should().BeInRange(4864, 5120);

        // And, by interpolation, they're monotonically non-decreasing.
        vm.P50.Should().BeLessThanOrEqualTo(vm.P95);
        vm.P95.Should().BeLessThanOrEqualTo(vm.P99);
    }

    [Fact]
    public void Throttles_property_updates_to_5Hz_under_sustained_emit()
    {
        // The VM gates PropertyChanged on Environment.TickCount64 deltas of
        // 200 ms (5 Hz). Emit 100 batches in a tight loop; the wall-clock
        // duration should be well under 200 ms on any reasonable CI machine,
        // so we expect the first emit to fire P50 once and subsequent emits
        // to be throttled. Even if the loop takes longer than expected, the
        // upper bound of 5 fires per second is a generous ceiling.
        var sampler = new FakeSampler();
        var vm = new LatencyHistogramViewModel(sampler);

        var p50Fires = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LatencyHistogramViewModel.P50))
            {
                p50Fires++;
            }
        };

        var start = Environment.TickCount64;
        for (var i = 0; i < 100; i++)
        {
            // Use a distinct latency per batch so the recomputed P50 actually
            // changes when the throttle gate opens — otherwise SetProperty
            // would suppress the event due to value equality and we'd be
            // measuring two things at once.
            sampler.Emit(new[] { new LatencySample(0, (i + 1) * 100L) });
        }
        var elapsedMs = Environment.TickCount64 - start;

        // Expected: at most ceil(elapsedMs / 200) + 1 fires. With 100 tight
        // emits the loop finishes in tens of milliseconds, so 1 fire is
        // typical and 5 is a comfortable ceiling for slow CI runners.
        p50Fires.Should().BeGreaterThan(0, "the first emit should always pass the throttle gate");
        p50Fires.Should().BeLessThanOrEqualTo(5, $"throttle gate at 5 Hz should suppress most of the 100 emits (elapsed: {elapsedMs} ms)");
        p50Fires.Should().BeLessThan(100, "PropertyChanged must be throttled, not fired on every emit");
    }

    [Fact]
    public void Buckets_reflect_observed_samples()
    {
        var sampler = new FakeSampler();
        var vm = new LatencyHistogramViewModel(sampler);

        var batch = new List<LatencySample>();
        for (var i = 0; i < 50; i++) batch.Add(new LatencySample(0, 100 * (i + 1)));
        sampler.Emit(batch);

        vm.Buckets.Should().NotBeNull();
        vm.Buckets.Sum().Should().Be(50);
    }

    [Fact]
    public void Disposing_unsubscribes_from_sampler()
    {
        var sampler = new FakeSampler();
        var vm = new LatencyHistogramViewModel(sampler);
        vm.Dispose();

        var changed = false;
        vm.PropertyChanged += (_, _) => changed = true;
        sampler.Emit(new[] { new LatencySample(0, 100) });

        changed.Should().BeFalse();
    }

    [Fact]
    public void Dispatches_ui_updates_through_provided_dispatcher()
    {
        // SamplesAdded fires on the sampler's drain thread; the VM must
        // marshal WPF-visible mutations through the injected dispatcher so it
        // never touches OxyPlot/INotifyPropertyChanged from a non-UI thread.
        var sampler = new FakeSampler();
        var dispatchCount = 0;
        var vm = new LatencyHistogramViewModel(sampler, action =>
        {
            dispatchCount++;
            action();
        });

        sampler.Emit(new[] { new LatencySample(0, 5000) });

        dispatchCount.Should().BeGreaterThan(0, "the first batch must traverse the dispatcher");
        vm.P50.Should().BeGreaterThan(0, "the dispatched action also applies the histogram percentiles");
    }
}
