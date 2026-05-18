using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Calibration;

namespace ApexMapper.Input.Abstractions.Hid;

public readonly record struct HidReportField(
    KeyId Key,
    int ByteOffset,
    int BitWidth,
    CalibrationCurve Curve);
