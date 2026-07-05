namespace ApexMapper.Input.Abstractions.Pipeline;

/// <summary>
/// One decoded keyboard transition. <see cref="DeviceId"/> is a per-device
/// tag assigned by the producing adapter, stable for that device for the
/// adapter's lifetime; 0 means the source device is unknown.
/// </summary>
public readonly record struct RawKeyEvent(
    ushort ScanCode,
    bool IsDown,
    long TimestampTicks,
    int DeviceId);
