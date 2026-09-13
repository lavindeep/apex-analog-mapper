using ApexMapper.Core.Keys;

namespace ApexMapper.Input.Hid;

/// <summary>Firmware 4.9.1 USBHIDtoADC indices translated to Windows Set 1 keys.</summary>
public static class ApexProSensorMap
{
    // Rows are the fourteen slots in D7 groups 1 through 5. Set 1 translation:
    // https://download.microsoft.com/download/1/6/1/161ba512-40e2-4cc9-843a-923143f3456c/translate.pdf
    // HID 31 and 32 both translate to 2B but occupy different layout-specific
    // sensors. Neither is admitted without a verified layout mapping. The Fn
    // usage F0 and the two unused slots also have no unambiguous Windows key.
    private static readonly ushort[] ScanCodes =
    [
        0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x7D,
        0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0,
        0x3A, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0, 0x1C,
        0x2A, 0x56, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x73, 0x36,
        0x1D, 0xE05B, 0x38, 0x7B, 0x39, 0x79, 0x70, 0xE038, 0xE05C, 0, 0xE01D, 0x0E, 0, 0,
    ];

    public static bool Supports(KeyId key) => TryGetSensor(key, out _, out _);

    internal static bool TryGetSensor(KeyId key, out int group, out int slot)
    {
        var index = key.ScanCode == 0 ? -1 : Array.IndexOf(ScanCodes, key.ScanCode);
        group = index < 0 ? 0 : index / 14 + 1;
        slot = index < 0 ? 0 : index % 14;
        return index >= 0;
    }
}
