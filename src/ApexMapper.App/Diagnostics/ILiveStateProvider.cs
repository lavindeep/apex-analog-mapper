namespace ApexMapper.App.Diagnostics;

/// <summary>
/// Periodically publishes <see cref="LiveStateSnapshot"/> values for the live
/// state diagnostics view.
/// </summary>
public interface ILiveStateProvider
{
    /// <summary>Raised whenever a new snapshot becomes available.</summary>
    event Action<LiveStateSnapshot>? SnapshotReady;

    /// <summary>Starts the snapshot loop at the given interval.</summary>
    void Start(TimeSpan interval, CancellationToken ct);

    /// <summary>Stops the snapshot loop. Safe to call when already stopped.</summary>
    void Stop();
}
