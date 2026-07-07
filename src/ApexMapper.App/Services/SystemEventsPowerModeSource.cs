using Microsoft.Win32;

namespace ApexMapper.App.Services;

/// <summary>
/// Binds <see cref="IPowerModeSource"/> to <see cref="SystemEvents.PowerModeChanged"/>,
/// the notification the OS raises on suspend/resume. Only <see cref="PowerModes.Resume"/>
/// is forwarded; suspend and status-change modes are ignored.
///
/// <para><see cref="SystemEvents.PowerModeChanged"/> is a <em>static</em> event: an
/// un-removed handler roots this instance for the life of the process, so
/// <see cref="Dispose"/> MUST detach it. The composition root owns this singleton
/// and disposes it on shutdown.</para>
/// </summary>
public sealed class SystemEventsPowerModeSource : IPowerModeSource
{
    private int _disposed;

    public SystemEventsPowerModeSource()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public event EventHandler? Resumed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }
}
