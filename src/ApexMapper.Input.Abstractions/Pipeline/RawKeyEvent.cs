namespace ApexMapper.Input.Abstractions.Pipeline;

public readonly record struct RawKeyEvent(
    ushort ScanCode,
    bool IsDown,
    long TimestampTicks,
    byte DeviceHandleIndex);
