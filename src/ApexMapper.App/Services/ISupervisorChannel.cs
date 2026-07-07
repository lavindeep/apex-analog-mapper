namespace ApexMapper.App.Services;

/// <summary>
/// App-side abstraction over the supervisor IPC channel. Production is
/// <see cref="SupervisorChannelBridge"/> (over the resilient channel adapter);
/// consumers depend on this interface so tests can substitute a fake.
/// Note the panic contract: with no live session, <see cref="SubmitPanicAsync"/>
/// completes as a silent fail-closed no-op — completion means "output forced
/// off", never proof the panic frame was delivered.
/// </summary>
public interface ISupervisorChannel : IDisposable
{
    bool IsConnected { get; }
    event EventHandler<SupervisorStatusEventArgs>? StatusChanged;

    Task ConnectAsync(CancellationToken ct);

    /// <summary>Sends zero output and disconnects from the supervisor.</summary>
    Task SubmitPanicAsync(CancellationToken ct);

    Task SubmitControlAsync(ApexMapper.Core.Pipeline.VirtualPadState state, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}

public sealed class SupervisorStatusEventArgs(bool isConnected, string? lastError) : EventArgs
{
    public bool IsConnected { get; } = isConnected;
    public string? LastError { get; } = lastError;
}
