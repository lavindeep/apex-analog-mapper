using System.ComponentModel;
using System.Runtime.CompilerServices;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace ApexMapper.App.Diagnostics.Latency;

/// <summary>
/// View-model for <see cref="LatencyHistogramView"/>. Subscribes to an
/// <see cref="ILatencySampler"/>, throttles UI refresh to 10 Hz, and exposes
/// P50/P95/P99 plus the live bucket array for chart binding.
///
/// <para>
/// The 10 Hz throttle is enforced by gating <see cref="OnSamplesAdded"/> on
/// <c>Environment.TickCount64</c> deltas: the sampler may emit dozens of
/// batches per second at 1 kHz drain, but the WPF dispatcher only sees
/// property-changed notifications every 100 ms.
/// </para>
///
/// <para>
/// Percentiles are cumulative since the view-model was created, not a rolling
/// window: the histogram is never reset. A rolling window becomes worthwhile
/// once the recorded value is true end-to-end latency (post-IPC wiring);
/// revisit then.
/// </para>
///
/// <para>
/// Threading: <see cref="ILatencySampler.SamplesAdded"/> fires on the sampler's
/// background drain thread. Histogram updates are thread-safe so they're done
/// inline, but every WPF-visible mutation (property setters, OxyPlot series
/// items, <c>InvalidatePlot</c>) is marshalled through the injected
/// <c>dispatch</c> callback. The default dispatcher invokes through the
/// current <see cref="System.Windows.Application.Dispatcher"/> when one is
/// available, falling back to synchronous execution for tests and headless
/// hosts.
/// </para>
/// </summary>
public sealed class LatencyHistogramViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Minimum interval between UI updates (10 Hz, per spec).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILatencySampler _sampler;
    private readonly HdrHistogramAdapter _histogram = new();
    private readonly Action<IReadOnlyList<LatencySample>> _samplesAdded;
    private readonly Action<Action> _dispatch;
    private readonly PlotModel _plotModel;
    private readonly BarSeries _barSeries;
    private long _lastRefreshTick;
    private double _p50;
    private double _p95;
    private double _p99;
    private bool _disposed;

    /// <summary>Creates a view-model bound to <paramref name="sampler"/> using the default WPF dispatcher.</summary>
    public LatencyHistogramViewModel(ILatencySampler sampler)
        : this(sampler, GetDefaultDispatcher())
    {
    }

    /// <summary>
    /// Creates a view-model bound to <paramref name="sampler"/> using
    /// <paramref name="dispatch"/> to marshal UI-visible updates. Tests pass a
    /// synchronous dispatcher (<c>action =&gt; action()</c>) to keep
    /// assertions deterministic.
    /// </summary>
    public LatencyHistogramViewModel(ILatencySampler sampler, Action<Action> dispatch)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(dispatch);
        _sampler = sampler;
        _dispatch = dispatch;
        _samplesAdded = OnSamplesAdded;
        (_plotModel, _barSeries) = BuildPlotModel();
        _sampler.SamplesAdded += _samplesAdded;
    }

    /// <summary>OxyPlot model bound to the chart in <c>LatencyHistogramView.xaml</c>.</summary>
    public PlotModel PlotModel => _plotModel;

    private static Action<Action> GetDefaultDispatcher()
    {
        // If a WPF Application is alive use its Dispatcher; otherwise execute
        // synchronously. The latter covers unit tests (which never set up an
        // Application) and any future headless hosts.
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is { } dispatcher)
        {
            return action => dispatcher.Invoke(action);
        }
        return action => action();
    }

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
        var buckets = _histogram.Buckets;
        _barSeries.Items.Clear();
        for (var i = 0; i < buckets.Count; i++)
        {
            _barSeries.Items.Add(new BarItem { Value = buckets[i] });
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

    /// <summary>
    /// Live histogram bucket counts, suitable for chart binding. Exposed as a
    /// stable getter that always returns the underlying buffer; consumers
    /// re-read the snapshot whenever a percentile property fires.
    /// </summary>
    public IReadOnlyList<long> Buckets => _histogram.Buckets;

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

        // Histogram is thread-safe — feed it inline on whatever thread fires.
        for (var i = 0; i < batch.Count; i++)
        {
            _histogram.RecordMicros(batch[i].LatencyMicros);
        }

        // Throttle UI updates to 10 Hz.
        var now = Environment.TickCount64;
        if (now - _lastRefreshTick < (long)RefreshInterval.TotalMilliseconds)
        {
            return;
        }
        _lastRefreshTick = now;

        var (p50, p95, p99) = _histogram.Percentiles();

        // Marshal every WPF-touching mutation through the dispatcher.
        _dispatch(() =>
        {
            P50 = p50;
            P95 = p95;
            P99 = p99;
            UpdatePlotSeries();
        });
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
