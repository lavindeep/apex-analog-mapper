using ApexMapper.Core.Keys;

namespace ApexMapper.Core.Sensors;

/// <summary>
/// Sensor index (group - 1) * 14 + slot to Windows scan code, in firmware order for
/// the gen 1 Apex Pro TKL. Slots with no key on a given layout read under 50 counts
/// at rest. Arrows and the function row are mechanical and have no sensor. Per-keyboard
/// overrides from the learn step take precedence over the table.
/// </summary>
public sealed class SensorMap
{
    /// <summary>Firmware order, five groups of fourteen. Zero marks a slot with no unambiguous Windows key.</summary>
    private static readonly ushort[] Defaults =
    [
        0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x7D,
        0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0,
        0x3A, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0, 0x1C,
        0x2A, 0x56, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x73, 0x36,
        0x1D, 0xE05B, 0x38, 0x7B, 0x39, 0x79, 0x70, 0xE038, 0xE05C, 0, 0xE01D, 0x0E, 0, 0,
    ];

    public static readonly SensorMap Default = new(new Dictionary<ScanCode, int>());

    private readonly Dictionary<ScanCode, int> _byKey;

    public SensorMap(IReadOnlyDictionary<ScanCode, int> overrides)
    {
        _byKey = new Dictionary<ScanCode, int>();
        for (var index = 0; index < Defaults.Length; index++)
        {
            if (Defaults[index] != 0)
            {
                _byKey[new ScanCode(Defaults[index])] = index;
            }
        }
        foreach (var (key, index) in overrides)
        {
            if (index is < 0 or >= SensorProtocol.SensorCount)
            {
                throw new ArgumentOutOfRangeException(nameof(overrides), index, "Sensor index must be 0..69.");
            }
            _byKey[key] = index;
        }
    }

    public bool TryGetSensorIndex(ScanCode key, out int index) => _byKey.TryGetValue(key, out index);

    public bool Supports(ScanCode key) => _byKey.ContainsKey(key);

    public int SupportedCount => _byKey.Count;

    public static int GroupOf(int sensorIndex) => sensorIndex / SensorProtocol.SensorsPerGroup + 1;

    public static int SlotOf(int sensorIndex) => sensorIndex % SensorProtocol.SensorsPerGroup;
}
