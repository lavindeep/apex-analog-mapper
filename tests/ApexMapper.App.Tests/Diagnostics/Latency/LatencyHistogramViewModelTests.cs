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
        var sampler = new FakeSampler { Percentiles = (1000, 5000, 9000) };
        var vm = new LatencyHistogramViewModel(sampler);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null) changed.Add(e.PropertyName);
        };

        sampler.Emit(new[] { new LatencySample(0, 1234) });

        vm.P50.Should().Be(1000);
        vm.P95.Should().Be(5000);
        vm.P99.Should().Be(9000);
        changed.Should().Contain(new[] { nameof(LatencyHistogramViewModel.P50), nameof(LatencyHistogramViewModel.P95), nameof(LatencyHistogramViewModel.P99) });
    }

    [Fact]
    public void Buckets_reflect_observed_samples()
    {
        var sampler = new FakeSampler { Percentiles = (10, 20, 30) };
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
        var sampler = new FakeSampler { Percentiles = (1, 2, 3) };
        var vm = new LatencyHistogramViewModel(sampler);
        vm.Dispose();

        var changed = false;
        vm.PropertyChanged += (_, _) => changed = true;
        sampler.Emit(new[] { new LatencySample(0, 100) });

        changed.Should().BeFalse();
    }
}
