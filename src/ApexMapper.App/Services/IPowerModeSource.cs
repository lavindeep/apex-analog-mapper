namespace ApexMapper.App.Services;

/// <summary>
/// A source of system power-mode notifications, narrowed to the one event the
/// mapper cares about: the machine has resumed from sleep or hibernate. Exists
/// so <see cref="ResumeGuard"/> can be driven deterministically in tests while
/// the real implementation binds the Windows-only power event.
/// </summary>
public interface IPowerModeSource : IDisposable
{
    /// <summary>Raised when the system resumes from a low-power state.</summary>
    event EventHandler? Resumed;
}
