namespace ApexMapper.App.Diagnostics;

/// <summary>
/// Summary of a HID device discovered by the manual adapter probe runner.
/// </summary>
public sealed record ProbeReport(
    string DeviceInstancePath,
    ushort VendorId,
    ushort ProductId,
    IReadOnlyList<ProbeInterface> Interfaces);
