using ApexMapper.Core;

namespace ApexMapper.App.Services;

/// <summary>Watches for foreground window changes and raises debounced events with process context.</summary>
public interface IForegroundWatcher : IDisposable
{
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    /// <summary>Returns the latest debounced foreground context.</summary>
    ForegroundContext Current { get; }

    void Start();
    void Stop();
}

public sealed class ForegroundChangedEventArgs(ForegroundContext context) : EventArgs
{
    public ForegroundContext Context { get; } = context;
}
