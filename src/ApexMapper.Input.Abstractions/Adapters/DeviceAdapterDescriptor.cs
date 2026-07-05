using ApexMapper.Input.Abstractions.Calibration;

namespace ApexMapper.Input.Abstractions.Adapters;

public enum HidReportType
{
    // Input is value 0 so an omitted report_type defaults to the safe input-report
    // path rather than the exploratory feature path. The enum serializes to JSON as
    // a snake_case string, so the underlying value never appears in adapter files
    // and reordering is round-trip-safe for existing descriptors.
    Input,
    Feature,
}

public sealed record DeviceMatch(
    int VendorId,
    int ProductId,
    ushort? UsagePage,
    string? ProductRegex,
    string? ManufacturerRegex,
    string? FirmwareVersion);

public sealed record InterfaceSelector(
    ushort? UsagePage,
    ushort? UsageId,
    string? CollectionPath);

public sealed record KeyMapEntry(
    ushort ScanCode,
    int ByteOffset,
    int BitWidth,
    NormalizationKind Normalization,
    int RawMin,
    int RawMax);

public sealed record ProbeHandshake(
    byte[] FeatureReportRequest,
    int ResponseTimeoutMs);

public sealed record AdapterCapabilities(
    bool Analog,
    bool PerKeyTravel);

public sealed record DeviceAdapterDescriptor(
    string SchemaVersion,
    string Id,
    string DisplayName,
    DeviceMatch Match,
    InterfaceSelector InterfaceSelector,
    byte ReportId,
    HidReportType ReportType,
    IReadOnlyList<KeyMapEntry> KeyMap,
    float NoiseFloor,
    float RestWindow,
    ProbeHandshake? ProbeHandshake,
    AdapterCapabilities Capabilities);
