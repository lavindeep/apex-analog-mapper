using ApexMapper.Core.Keys;
using ApexMapper.Persistence.Devices;
using ApexMapper.Persistence.Recovery;
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

    [Fact]
    public void Second_save_creates_a_rolling_backup()
    {
        var reg = new DeviceRegistry(new DeviceIdentity(1, 1, null, null, null), Array.Empty<KeyCalibration>());
        DeviceRegistry.Save(Path, reg);
        DeviceRegistry.Save(Path, reg);
        File.Exists(Path + ".bak.1").Should().BeTrue();
    }

    [Fact]
    public void Corrupt_registry_recovers_from_backup_and_quarantines_the_corrupt_file()
    {
        var reg = new DeviceRegistry(new DeviceIdentity(0x1038, 0x161C, null, null, null), Array.Empty<KeyCalibration>());
        DeviceRegistry.Save(Path, reg);
        DeviceRegistry.Save(Path, reg); // bak.1 is a good copy
        File.WriteAllText(Path, "{ not valid json");

        var loaded = DeviceRegistry.Load(Path, out var recovery);

        loaded.SelectedDevice.Should().Be(reg.SelectedDevice);
        File.Exists(Path + ".corrupt").Should().BeTrue();
        recovery.Should().NotBeNull();
        recovery!.Outcome.Should().Be(RecoveryOutcome.RecoveredFromBackup);
    }

    [Fact]
    public void All_copies_corrupt_defaults_and_reports_quarantine_without_deleting()
    {
        var reg = new DeviceRegistry(new DeviceIdentity(1, 1, null, null, null), Array.Empty<KeyCalibration>());
        DeviceRegistry.Save(Path, reg);
        DeviceRegistry.Save(Path, reg); // bak.1 exists
        File.WriteAllText(Path, "{ bad");
        File.WriteAllText(Path + ".bak.1", "{ also bad");

        var loaded = DeviceRegistry.Load(Path, out var recovery);

        loaded.SelectedDevice.Should().BeNull();
        loaded.Calibrations.Should().BeEmpty();
        recovery!.Outcome.Should().Be(RecoveryOutcome.Quarantined);
        File.Exists(Path + ".corrupt").Should().BeTrue();
        File.Exists(Path + ".bak.1").Should().BeTrue();
    }

    [Fact]
    public void Newer_schema_is_reported_and_left_untouched()
    {
        var newer = "{\"version\": 999, \"payload\": null}";
        File.WriteAllText(Path, newer);

        var loaded = DeviceRegistry.Load(Path, out var recovery);

        loaded.SelectedDevice.Should().BeNull();
        recovery!.Outcome.Should().Be(RecoveryOutcome.NewerSchema);
        File.Exists(Path + ".corrupt").Should().BeFalse();
        File.ReadAllText(Path).Should().Be(newer);
    }

    [Fact]
    public void Save_refuses_to_overwrite_a_newer_schema_file()
    {
        var newer = "{\"version\": 999, \"payload\": null}";
        File.WriteAllText(Path, newer);

        var act = () => DeviceRegistry.Save(Path, new DeviceRegistry(new DeviceIdentity(1, 1, null, null, null), Array.Empty<KeyCalibration>()));

        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path).Should().Be(newer);
    }
}
