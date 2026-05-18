namespace ApexMapper.App.Services;

/// <summary>Captures and persists axis calibration snapshots from a connected device.</summary>
public interface ICalibrationService
{
    Task<CalibrationSnapshot> CaptureRestAsync(System.Guid deviceId, CancellationToken ct);
    Task<CalibrationSnapshot> CaptureMaxAsync(System.Guid deviceId, CancellationToken ct);
    Task<CalibrationSnapshot> CaptureNoiseAsync(System.Guid deviceId, CancellationToken ct);
    Task PersistAsync(
        System.Guid deviceId,
        CalibrationSnapshot rest,
        CalibrationSnapshot max,
        CalibrationSnapshot noise,
        CancellationToken ct);
}

/// <summary>
/// A point-in-time sample of per-key raw ADC values keyed by HID report-id byte.
/// The key type may be tightened once the real HID report descriptor is confirmed.
/// </summary>
public sealed record CalibrationSnapshot(
    IReadOnlyDictionary<byte, ushort> PerKeySamples,
    System.DateTimeOffset CapturedAt);
