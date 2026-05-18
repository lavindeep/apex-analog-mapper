namespace ApexMapper.Input.Abstractions.Hid;

/// <summary>
/// HID report-descriptor access mode for a single data field. Mirrors the
/// three Main-item operators emitted by <c>HidReportDescriptorParser</c>.
/// </summary>
/// <remarks>
/// Kept in <c>ApexMapper.Input.Abstractions</c> so the descriptor parser can
/// run cross-platform. The App-side probe runner maps these onto its own
/// <c>ProbeAccessMode</c> at the boundary; the two enums intentionally have
/// the same shape but the App layer owns its public surface.
/// </remarks>
public enum HidAccessMode
{
    Input,
    Output,
    Feature,
}
