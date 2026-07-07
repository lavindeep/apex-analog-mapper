using ApexMapper.Core.Pipeline;
using ApexMapper.Output.Ipc;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Services;

/// <summary>
/// Bridges the App's <see cref="ISupervisorChannel"/> abstraction 1:1 onto the
/// resilient <see cref="SupervisorChannelAdapter"/>, which owns the connection
/// lifecycle and the control/heartbeat cadence.
///
/// Semantics carried over from the adapter:
/// <list type="bullet">
/// <item><see cref="ConnectAsync"/> enables the channel and returns immediately;
/// the session is established in the background with retry-forever, capped
/// backoff. Observe <see cref="StatusChanged"/> or <see cref="IsConnected"/> for
/// the outcome.</item>
/// <item><see cref="SubmitControlAsync"/> writes the latest-wins pad-state slot;
/// the adapter's own cadence delivers it. The mapping engine normally bypasses
/// this method and pushes straight into <see cref="Sink"/>.</item>
/// <item><see cref="SubmitPanicAsync"/> forwards the panic and locally
/// disconnects with NO auto-reconnect until the next <see cref="ConnectAsync"/>.
/// With no live session it completes as a silent fail-closed no-op — completion
/// proves "output forced off", never "panic frame delivered".</item>
/// </list>
/// </summary>
public sealed class SupervisorChannelBridge : ISupervisorChannel
{
    private readonly SupervisorChannelAdapter _adapter;
    private readonly ILogger<SupervisorChannelBridge>? _logger;
    private int _disposed;

    public SupervisorChannelBridge(
        string sessionId,
        SupervisorChannelOptions? options = null,
        ILogger<SupervisorChannelBridge>? logger = null)
    {
        _adapter = new SupervisorChannelAdapter(sessionId, options);
        _logger = logger;
        _adapter.StatusChanged += OnAdapterStatusChanged;
    }

    /// <summary>
    /// The latest-wins pad-state slot the mapping engine pushes into on every
    /// tick; the adapter snapshots it on each control interval.
    /// </summary>
    public IPadStateSink Sink => _adapter;

    public bool IsConnected => _adapter.IsConnected;

    public event EventHandler<SupervisorStatusEventArgs>? StatusChanged;

    public Task ConnectAsync(CancellationToken ct)
    {
        _adapter.Start();
        return Task.CompletedTask;
    }

    public Task SubmitControlAsync(VirtualPadState state, CancellationToken ct)
    {
        _adapter.SetState(in state);
        return Task.CompletedTask;
    }

    public Task SubmitPanicAsync(CancellationToken ct)
        => _adapter.SubmitPanicAsync("user panic", ct);

    public Task DisconnectAsync(CancellationToken ct)
        => _adapter.DisconnectAsync(ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _adapter.StatusChanged -= OnAdapterStatusChanged;
        // Bounded inside the adapter (best-effort zero is capped at 250 ms), so
        // this sync-over-async cannot wedge shutdown.
        _adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void OnAdapterStatusChanged(bool connected, Exception? error)
    {
        if (connected)
        {
            _logger?.LogInformation("Supervisor session established.");
        }
        else if (error is not null)
        {
            _logger?.LogWarning(error, "Supervisor session lost.");
        }
        else
        {
            _logger?.LogInformation("Supervisor session ended.");
        }

        StatusChanged?.Invoke(this, new SupervisorStatusEventArgs(connected, error?.Message));
    }
}
