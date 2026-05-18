using ApexMapper.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Composition;

/// <summary>
/// Placeholder factory that constructs <see cref="StubSupervisorChannel"/>.
///
/// TODO (Phase 3 integration): replace <see cref="StubSupervisorChannel"/> with
/// the real <c>SupervisorClient</c> once Phase 3 is merged.  The factory pattern
/// keeps the composition root stable — only this file needs updating.
/// </summary>
public static class SupervisorClientFactory
{
    /// <summary>
    /// Creates the <see cref="ISupervisorChannel"/> for the current session.
    /// Currently returns a <see cref="StubSupervisorChannel"/>; swap here for
    /// the real client when Phase 3 lands.
    /// </summary>
    public static ISupervisorChannel Create(IServiceProvider sp)
    {
        var logger = sp.GetRequiredService<ILogger<StubSupervisorChannel>>();
        return new StubSupervisorChannel(logger);
    }
}
