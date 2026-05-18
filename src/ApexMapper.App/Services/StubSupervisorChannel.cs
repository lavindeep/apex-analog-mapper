using ApexMapper.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Services;

/// <summary>
/// No-op <see cref="ISupervisorChannel"/> bridge used until Phase 3 is integrated.
/// All operations are logged at Information level and return immediately.
/// Swap this for the real <c>SupervisorClient</c> in <c>SupervisorClientFactory</c>
/// once Phase 3 lands.
/// </summary>
public sealed class StubSupervisorChannel : ISupervisorChannel
{
    private readonly ILogger<StubSupervisorChannel> _logger;

    public StubSupervisorChannel(ILogger<StubSupervisorChannel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConnected => false;

    // Never raised — stub has no real supervisor to connect to.
    public event EventHandler<SupervisorStatusEventArgs>? StatusChanged;

    public Task ConnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("StubSupervisorChannel.ConnectAsync called (no-op).");
        return Task.CompletedTask;
    }

    public Task SubmitPanicAsync(CancellationToken ct)
    {
        _logger.LogInformation("StubSupervisorChannel.SubmitPanicAsync called (no-op).");
        return Task.CompletedTask;
    }

    public Task SubmitControlAsync(VirtualPadState state, CancellationToken ct)
    {
        _logger.LogInformation("StubSupervisorChannel.SubmitControlAsync called (no-op).");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("StubSupervisorChannel.DisconnectAsync called (no-op).");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _logger.LogInformation("StubSupervisorChannel.Dispose called.");
    }
}
