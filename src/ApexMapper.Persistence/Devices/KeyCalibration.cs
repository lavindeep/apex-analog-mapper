using ApexMapper.Core.Keys;

namespace ApexMapper.Persistence.Devices;

/// <summary>
/// A per-key measured calibration. All three values are in <b>raw ADC units</b>
/// (the same domain as the adapter's raw_min/raw_max and the raw field value the
/// HID parser feeds into the curve) — not normalized 0..1. They are consumed as
/// <c>CalibrationCurve</c> endpoints by
/// <c>DeviceAdapterStore.ToCalibrationOverrides</c>, so storing normalized values
/// would clamp every reading to full press.
/// </summary>
public sealed record KeyCalibration(
    KeyId Key,
    float RestValue,
    float MaxPressValue,
    float NoiseBand);
