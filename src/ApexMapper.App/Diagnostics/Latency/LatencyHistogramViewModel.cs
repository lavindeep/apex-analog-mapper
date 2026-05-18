using System.ComponentModel;
using System.Runtime.CompilerServices;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace ApexMapper.App.Diagnostics.Latency;

/// <summary>
/// View-model for <see cref="LatencyHistogramView"/>. Subscribes to an
/// <see cref="ILatencySampler"/>, throttles UI refresh to ~5 Hz, and exposes
/// P50/P95/P99 plus the live bucket array for chart binding.
///
/// <para>
/// The 5 Hz throttle is enforced by gating <see cref="OnSamplesAdded"/> on
/// <c>Environment.TickCount64</c> deltas: the sampler may emit dozens of
/// batches per second at 1 kHz drain, but the WPF dispatcher only sees
/// property-changed notifications every 200 ms.
/// </para>
/// </summary>
public sealed class LatencyHistogramViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Minimum interval between UI updates (5 Hz).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);

    private readonly ILatencySampler _sampler;
    private readonly HdrHistogramAdapter _histogram = new();
    private readonly Action<IReadOnlyList<LatencySample>> _samplesAdded;
    private readonly PlotModel _plotModel;
    private readonly BarSeries _barSeries;
    private long _lastRefreshTick;
    private double _p50;
    private double _p95;
    private double _p99;
    private IReadOnlyList<long> _buckets;
    private bool _disposed;

    /// <summary>Creates a view-model bound to <paramref name="sampler"/>.</summary>
    public LatencyHistogramViewModel(ILatencySampler sampler)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        _sampler = sampler;
        _buckets = _histogram.Buckets;
        _samplesAdded = OnSamplesAdded;
        (_plotModel, _barSeries) = BuildPlotModel();
        _sampler.SamplesAdded += _samplesAdded;
    }

    /// <summary>OxyPlot model bound to the chart in <c>LatencyHistogramView.xaml</c>.</summary>
    public PlotModel PlotModel => _plotModel;

    private static (PlotModel Model, BarSeries Series) BuildPlotModel()
    {
        var model = new PlotModel { Title = "Latency (µs) — log buckets" };
        var series = new BarSeries
        {
            StrokeThickness = 0,
            FillColor = OxyColors.SteelBlue,
        };
        model.Series.Add(series);
        model.Axes.Add(new CategoryAxis { Position = AxisPosition.Left, Title = "Bucket" });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Count", Minimum = 0 });
        return (model, series);
    }

    private void UpdatePlotSeries()
    {
        _barSeries.Items.Clear();
        for (var i = 0; i < _buckets.Count; i++)
        {
            _barSeries.Items.Add(new BarItem { Value = _buckets[i] });
        }
        _plotModel.InvalidatePlot(updateData: true);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>P50 latency in microseconds.</summary>
    public double P50
    {
        get => _p50;
        private set => SetProperty(ref _p50, value);
    }

    /// <summary>P95 latency in microseconds.</summary>
    public double P95
    {
        get => _p95;
        private set => SetProperty(ref _p95, value);
    }

    /// <summary>P99 latency in microseconds.</summary>
    public double P99
    {
        get => _p99;
        private set => SetProperty(ref _p99, value);
    }

    /// <summary>Live histogram bucket counts, suitable for chart binding.</summary>
    public IReadOnlyList<long> Buckets
    {
        get => _buckets;
        private set => SetProperty(ref _buckets, value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _sampler.SamplesAdded -= _samplesAdded;
    }

    private void OnSamplesAdded(IReadOnlyList<LatencySample> batch)
    {
        if (_disposed)
        {
            return;
        }

        // Always feed the local histogram so bucket totals are accurate.
        for (var i = 0; i < batch.Count; i++)
        {
            _histogram.RecordMicros(batch[i].LatencyMicros);
        }

        // Throttle property-changed firings to 5 Hz.
        var now = Environment.TickCount64;
        if (now - _lastRefreshTick < (long)RefreshInterval.TotalMilliseconds)
        {
            return;
        }
        _lastRefreshTick = now;

        var (p50, p95, p99) = _histogram.Percentiles();
        P50 = p50;
        P95 = p95;
        P99 = p99;
        Buckets = _histogram.Buckets;
        UpdatePlotSeries();
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
