namespace ApexMapper.App.Services;

/// <summary>
/// Phase-3 stand-in abstraction; Phase 4 consumers depend on this rather than on
/// SupervisorClient directly. The integrate step will wire Phase 3's real client through it.
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
