namespace ApexMapper.App.Diagnostics;

/// <summary>
/// Enumerates HID devices and produces probe reports for the manual adapter
/// discovery UI.
/// </summary>
public interface IProbeRunner
{
    /// <summary>Synchronously enumerates connected HID devices and probes them.</summary>
    IReadOnlyList<ProbeReport> Probe();
}
