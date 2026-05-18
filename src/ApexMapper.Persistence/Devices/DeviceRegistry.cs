using ApexMapper.Persistence.Atomic;
using ApexMapper.Persistence.Json;

namespace ApexMapper.Persistence.Devices;

public sealed record DeviceRegistry(
    DeviceIdentity? SelectedDevice,
    IReadOnlyList<KeyCalibration> Calibrations)
{
    public const int CurrentSchemaVersion = 1;

    public static DeviceRegistry Load(string path)
    {
        if (!File.Exists(path)) return new DeviceRegistry(null, Array.Empty<KeyCalibration>());
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerialization.Deserialize<VersionedDocument<DeviceRegistry>>(json);
            if (doc is null || doc.Version != CurrentSchemaVersion || doc.Payload is null)
            {
                return new DeviceRegistry(null, Array.Empty<KeyCalibration>());
            }
            return doc.Payload;
        }
        catch
        {
            return new DeviceRegistry(null, Array.Empty<KeyCalibration>());
        }
    }

    public static void Save(string path, DeviceRegistry registry)
    {
        var doc = new VersionedDocument<DeviceRegistry>(CurrentSchemaVersion, registry);
        AtomicFile.WriteAllText(path, JsonSerialization.Serialize(doc));
    }
}
