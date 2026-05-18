namespace ApexMapper.Input.Abstractions.Hid;

/// <summary>
/// A flat record emitted by <see cref="HidReportDescriptorParser"/> for each
/// Main item (Input / Output / Feature) encountered while walking a HID
/// report descriptor.
/// </summary>
/// <param name="ReportId">
/// Active Report ID at the time of the item. Zero when the descriptor never
/// emits a Global Report ID (single-report device).
/// </param>
/// <param name="AccessMode">Which Main operator produced this field.</param>
/// <param name="BitOffset">
/// Cumulative bit offset within the report identified by
/// <paramref name="ReportId"/>. Offsets reset to zero whenever the active
/// Report ID changes (per USB HID class spec §6.2.2.7).
/// </param>
/// <param name="BitSize">
/// Combined width of the item in bits, i.e. <c>Report Size × Report Count</c>.
/// </param>
/// <param name="UsagePage">Most recent Global Usage Page (default 0).</param>
/// <param name="Usage">
/// Most recent Local Usage applied to the item, or 0 if none was set.
/// </param>
public readonly record struct HidReportDescriptorField(
    byte ReportId,
    HidAccessMode AccessMode,
    int BitOffset,
    int BitSize,
    ushort UsagePage,
    uint Usage);
