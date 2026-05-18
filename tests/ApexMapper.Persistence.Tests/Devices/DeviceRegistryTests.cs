using ApexMapper.Core.Keys;
using ApexMapper.Persistence.Devices;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Devices;

public class DeviceRegistryTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apex-reg-" + Guid.NewGuid().ToString("N"));

    public DeviceRegistryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Path => System.IO.Path.Combine(_dir, "registry.json");

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        var reg = DeviceRegistry.Load(Path);
        reg.SelectedDevice.Should().BeNull();
        reg.Calibrations.Should().BeEmpty();
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var device = new DeviceIdentity(VendorId: 0x1038, ProductId: 0x161C, SerialNumber: "ABC", ManufacturerName: "SteelSeries", ProductName: "Apex Pro");
        var cal = new KeyCalibration(KeyId.FromScanCode(0x11), RestValue: 0.02f, MaxPressValue: 0.94f, NoiseBand: 0.01f);
        var reg = new DeviceRegistry(device, new[] { cal });
        DeviceRegistry.Save(Path, reg);

        var loaded = DeviceRegistry.Load(Path);
        loaded.SelectedDevice.Should().Be(device);
        loaded.Calibrations.Should().ContainSingle();
        loaded.Calibrations[0].Should().Be(cal);
    }

    [Fact]
    public void Save_atomically_replaces_existing()
    {
        DeviceRegistry.Save(Path, new DeviceRegistry(null, Array.Empty<KeyCalibration>()));
        var device = new DeviceIdentity(1, 1, null, null, null);
        DeviceRegistry.Save(Path, new DeviceRegistry(device, Array.Empty<KeyCalibration>()));
        DeviceRegistry.Load(Path).SelectedDevice.Should().Be(device);
    }
}
