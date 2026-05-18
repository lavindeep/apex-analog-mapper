namespace ApexMapper.App.Diagnostics;

/// <summary>
/// Periodically drains samples from the binding pipeline's latency recorder
/// and exposes them with running percentiles for diagnostic display.
/// </summary>
public interface ILatencySampler
{
    /// <summary>Starts the sampling loop at the given interval.</summary>
    void Start(TimeSpan interval, CancellationToken ct);

    /// <summary>Stops the sampling loop. Safe to call when already stopped.</summary>
    void Stop();

    /// <summary>Running P50/P95/P99 latency in microseconds over the configured window.</summary>
    (double P50, double P95, double P99) Percentiles { get; }

    /// <summary>Raised whenever a new batch of samples is drained.</summary>
    event Action<IReadOnlyList<LatencySample>>? SamplesAdded;
}
