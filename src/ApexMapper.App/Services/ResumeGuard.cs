using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Services;

/// <summary>
/// Subscribes to system resume notifications and, on each resume, asks the
/// mapping session to gate currently-held keys (<see cref="IMappingSession.OnSystemResumed"/>).
/// The policy itself lives in the session; this service is only the event
/// plumbing, so the behavior can be exercised through a fake
/// <see cref="IPowerModeSource"/> without a real power event.
///
/// <para>Lifecycle: <see cref="Start"/> subscribes, <see cref="Dispose"/>
/// unsubscribes. The underlying source binds a static OS event, so a leaked
/// subscription would outlive the app — the composition root disposes both this
/// service and the source on shutdown.</para>
/// </summary>
public sealed class ResumeGuard : IDisposable
{
    private readonly IPowerModeSource _source;
    private readonly IMappingSession _session;
    private readonly ILogger<ResumeGuard> _logger;
    private int _started;
    private int _disposed;

    public ResumeGuard(IPowerModeSource source, IMappingSession session, ILogger<ResumeGuard> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Begins listening for resume events. Idempotent.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _source.Resumed += OnResumed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.Resumed -= OnResumed;
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        try
        {
            _session.OnSystemResumed();
        }
        catch (Exception ex)
        {
            // A resume notification must never take the process down; the session
            // gate is best-effort safety, and the next transition re-gates anyway.
            _logger.LogWarning(ex, "Handling a system-resume notification failed.");
        }
    }
}
