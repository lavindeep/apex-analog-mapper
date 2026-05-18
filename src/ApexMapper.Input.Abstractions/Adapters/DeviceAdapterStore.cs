using System.Reflection;
using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Input.Abstractions.Hid;
using ApexMapper.Persistence.Json;

namespace ApexMapper.Input.Abstractions.Adapters;

public static class DeviceAdapterStore
{
    public const string CurrentSchemaVersion = "1";

    public static DeviceAdapterDescriptor LoadEmbedded(string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        var assembly = typeof(DeviceAdapterStore).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"Embedded resource '{resourceName}' not found in {assembly.GetName().Name}.",
                resourceName);
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return Parse(json);
    }

    public static DeviceAdapterDescriptor LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public static IReadOnlyList<HidReportField> ToFields(
        DeviceAdapterDescriptor descriptor,
        IReadOnlyDictionary<KeyId, CalibrationCurve>? calibrationOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var keyMap = descriptor.KeyMap;
        var fields = new HidReportField[keyMap.Count];
        for (var i = 0; i < keyMap.Count; i++)
        {
            var entry = keyMap[i];
            var key = KeyId.FromScanCode(entry.ScanCode);

            CalibrationCurve curve;
            if (calibrationOverrides is not null && calibrationOverrides.TryGetValue(key, out var overrideCurve))
            {
                curve = overrideCurve;
            }
            else
            {
                var span = MathF.Abs((float)entry.RawMax - entry.RawMin);
                curve = new CalibrationCurve(
                    Rest: entry.RawMin,
                    Max: entry.RawMax,
                    NoiseBand: descriptor.NoiseFloor * span,
                    Kind: entry.Normalization);
            }

            fields[i] = new HidReportField(key, entry.ByteOffset, entry.BitWidth, curve);
        }

        return fields;
    }

    private static DeviceAdapterDescriptor Parse(string json)
    {
        var descriptor = JsonSerialization.Deserialize<DeviceAdapterDescriptor>(json)
            ?? throw new InvalidDataException("Device adapter descriptor JSON was null or empty.");

        if (!string.Equals(descriptor.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Device adapter schema_version '{descriptor.SchemaVersion}' is not supported; expected '{CurrentSchemaVersion}'.");
        }

        if (descriptor.Match.VendorId <= 0 || descriptor.Match.ProductId <= 0)
        {
            throw new InvalidDataException(
                $"Device adapter '{descriptor.Id}' has invalid match.vendor_id/product_id (must be > 0).");
        }

        var keyMap = descriptor.KeyMap;
        if (keyMap.Count > 1)
        {
            var seen = new HashSet<ushort>(keyMap.Count);
            for (var i = 0; i < keyMap.Count; i++)
            {
                if (!seen.Add(keyMap[i].ScanCode))
                {
                    throw new InvalidDataException(
                        $"Device adapter '{descriptor.Id}' contains duplicate key_map scan_code 0x{keyMap[i].ScanCode:X4}.");
                }
            }
        }

        return descriptor;
    }
}
