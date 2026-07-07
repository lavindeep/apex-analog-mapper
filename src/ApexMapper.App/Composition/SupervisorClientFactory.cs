using ApexMapper.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Composition;

/// <summary>
/// Builds the production <see cref="ISupervisorChannel"/>: a
/// <see cref="SupervisorChannelBridge"/> over the resilient supervisor channel
/// for the current session. The factory pattern keeps the composition root
/// stable — channel construction details live only in this file.
/// </summary>
public static class SupervisorClientFactory
{
    public static ISupervisorChannel Create(IServiceProvider sp)
    {
        var sessionId = sp.GetRequiredService<SupervisorSessionId>();
        var logger = sp.GetRequiredService<ILogger<SupervisorChannelBridge>>();
        return new SupervisorChannelBridge(sessionId.Value, options: null, logger);
    }
}
