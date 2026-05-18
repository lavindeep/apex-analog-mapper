namespace ApexMapper.App.Diagnostics;

/// <summary>
/// A single HID report descriptor field discovered during manual adapter
/// probing.
/// </summary>
public sealed record ProbeInterface(
    byte ReportId,
    ProbeAccessMode AccessMode,
    int BitOffset,
    int BitSize);
