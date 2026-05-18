namespace ApexMapper.Input.Abstractions.Backends;

public interface IInputBackend : IAsyncDisposable
{
    BackendStatus Status { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    event EventHandler<BackendStatusChanged>? StatusChanged;
}
