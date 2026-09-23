using System.IO;
using ApexMapper.App.Storage;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.App.Tests.Storage;

public class CalibrationStoreTests
{
    private static readonly Guid Tkl = new("27373de1-4206-11f1-b9e4-14ac60fcc13e");
    private static readonly Guid Other = new("11111111-2222-3333-4444-555555555555");
    private static readonly KeyCalibration WCal = KeyCalibration.Create(1900, 4095, 40, 16);
    private static readonly KeyCalibration SCal = KeyCalibration.Create(2100, 300, 45, 30);

    private static string FileOf(TempDirectory dir, Guid keyboard) => dir.File(keyboard.ToString("D") + ".json");

    [Fact]
    public void Nothing_saved_is_empty_without_a_problem()
    {
        using var dir = new TempDirectory();

        var loaded = new CalibrationStore(dir.Path).Load(Tkl, "4.16.8");

        Assert.Empty(loaded.Keys);
        Assert.Empty(loaded.FromOtherFirmware);
        Assert.Empty(loaded.Signatures);
        Assert.Null(loaded.Problem);
    }

    [Fact]
    public void Keys_round_trip_per_keyboard_in_a_file_named_by_its_container_id()
    {
        using var dir = new TempDirectory();
        var store = new CalibrationStore(dir.Path);

        store.Put(Tkl, "4.16.8", DefaultProfiles.Key.W, WCal);
        store.Put(Tkl, "4.16.8", DefaultProfiles.Key.S, SCal);
        store.Put(Other, "4.16.8", DefaultProfiles.Key.W, SCal with { SensorIndex = 16 });

        var tkl = store.Load(Tkl, "4.16.8");
        Assert.Equal(WCal, tkl.Keys[DefaultProfiles.Key.W]);
        Assert.Equal(SCal, tkl.Keys[DefaultProfiles.Key.S]);
        Assert.Empty(tkl.FromOtherFirmware);
        Assert.Single(store.Load(Other, "4.16.8").Keys);
        Assert.True(File.Exists(dir.File("27373de1-4206-11f1-b9e4-14ac60fcc13e.json")));
    }

    [Fact]
    public void Keys_calibrated_on_other_firmware_still_load_and_are_flagged_until_calibrated_again()
    {
        using var dir = new TempDirectory();
        var store = new CalibrationStore(dir.Path);
        store.Put(Tkl, "4.9.1", DefaultProfiles.Key.W, WCal);
        store.Put(Tkl, "4.9.1", DefaultProfiles.Key.S, SCal);

        var afterUpdate = store.Load(Tkl, "4.16.8");
        Assert.Equal(2, afterUpdate.Keys.Count);
        Assert.Equal([DefaultProfiles.Key.W, DefaultProfiles.Key.S], afterUpdate.FromOtherFirmware.OrderBy(k => k.Value));

        store.Put(Tkl, "4.16.8", DefaultProfiles.Key.W, WCal with { Rest = 1910 });
        var recalibrated = store.Load(Tkl, "4.16.8");
        Assert.Equal([DefaultProfiles.Key.S], recalibrated.FromOtherFirmware);
        Assert.Equal(1910, recalibrated.Keys[DefaultProfiles.Key.W].Rest);
    }

    [Fact]
    public void Group_signatures_round_trip_beside_the_keys_and_only_the_current_firmware_s_count()
    {
        using var dir = new TempDirectory();
        var store = new CalibrationStore(dir.Path);
        store.Put(Tkl, "4.16.8", DefaultProfiles.Key.W, WCal);
        store.PutSignature(Tkl, "4.16.8", 2, new GroupSignature(0b0010_0000_0001));
        store.PutSignature(Tkl, "4.9.1", 3, new GroupSignature(0b0100));
        store.PutSignature(Tkl, "4.16.8", 2, new GroupSignature(0b0010_0000_0011));

        var loaded = store.Load(Tkl, "4.16.8");

        Assert.Equal(new Dictionary<int, GroupSignature> { [2] = new(0b0010_0000_0011) }, loaded.Signatures);
        Assert.Equal(WCal, loaded.Keys[DefaultProfiles.Key.W]);
        Assert.Equal(new Dictionary<int, GroupSignature> { [3] = new(0b0100) }, store.Load(Tkl, "4.9.1").Signatures);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.PutSignature(Tkl, "4.16.8", 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.PutSignature(Tkl, "4.16.8", SensorRequest.GroupCount + 1, default));
    }

    [Fact]
    public void A_file_from_before_signatures_loads_with_none()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(FileOf(dir, Tkl), """
            { "version": 1, "payload": { "keys": {
              "0x11": { "rest": 1900, "full_press": 4095, "noise_band": 40, "sensor_index": 16, "firmware": "4.16.8" }
            } } }
            """);

        var loaded = new CalibrationStore(dir.Path).Load(Tkl, "4.16.8");

        Assert.Equal([DefaultProfiles.Key.W], loaded.Keys.Keys);
        Assert.Empty(loaded.Signatures);
        Assert.Null(loaded.Problem);
    }

    [Fact]
    public void Invalid_and_empty_entries_are_left_out_and_reported_and_the_rest_load()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(FileOf(dir, Tkl), """
            { "version": 1, "payload": { "keys": {
              "0x11": { "rest": 1900, "full_press": 4095, "noise_band": 40, "sensor_index": 16, "firmware": "4.16.8" },
              "0x1F": { "rest": 2100, "full_press": 2150, "noise_band": 40, "sensor_index": 99, "firmware": "4.16.8" },
              "0x1E": null
            } } }
            """);

        var loaded = new CalibrationStore(dir.Path).Load(Tkl, "4.16.8");

        Assert.Equal([DefaultProfiles.Key.W], loaded.Keys.Keys);
        Assert.Equal("2 keys' calibrations were invalid and must be calibrated again.", loaded.Problem);
    }

    [Fact]
    public void A_restored_backup_with_an_invalid_key_reports_both()
    {
        using var dir = new TempDirectory();
        var path = FileOf(dir, Tkl);
        File.WriteAllText(path + ".bak", """
            { "version": 1, "payload": { "keys": {
              "0x11": { "rest": 1900, "full_press": 4095, "noise_band": 40, "sensor_index": 99, "firmware": "4.16.8" }
            } } }
            """);
        File.WriteAllText(path, "{ not json");

        var problem = new CalibrationStore(dir.Path).Load(Tkl, "4.16.8").Problem;

        Assert.Contains("its backup was used", problem);
        Assert.Contains("One key's calibration was invalid and must be calibrated again.", problem);
    }

    [Fact]
    public void Put_refuses_an_invalid_calibration_and_a_file_it_cannot_read()
    {
        using var dir = new TempDirectory();
        var store = new CalibrationStore(dir.Path);
        store.Put(Tkl, "4.16.8", DefaultProfiles.Key.W, WCal);

        Assert.Throws<ArgumentException>(() => store.Put(Tkl, "4.16.8", DefaultProfiles.Key.S, SCal with { SensorIndex = 99 }));

        // Held without read sharing but with delete sharing: the file cannot be read, yet
        // could be replaced. The stored W must survive.
        using (new FileStream(FileOf(dir, Tkl), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Contains("could not be opened", store.Load(Tkl, "4.16.8").Problem);
            Assert.Throws<IOException>(() => store.Put(Tkl, "4.16.8", DefaultProfiles.Key.S, SCal));
            Assert.Throws<IOException>(() => store.PutSignature(Tkl, "4.16.8", 2, default));
        }
        Assert.Equal([DefaultProfiles.Key.W], store.Load(Tkl, "4.16.8").Keys.Keys);
    }

    [Fact]
    public void A_file_from_a_newer_version_loads_nothing_and_is_never_written_over()
    {
        using var dir = new TempDirectory();
        var path = FileOf(dir, Tkl);
        var newer = """
            { "version": 2, "payload": { "keys": {
              "0x11": { "rest": 1900, "full_press": 4095, "noise_band": 40, "sensor_index": 16, "firmware": "4.16.8" }
            } } }
            """;
        File.WriteAllText(path, newer);
        var store = new CalibrationStore(dir.Path);

        var loaded = store.Load(Tkl, "4.16.8");

        Assert.Empty(loaded.Keys);
        Assert.Contains("newer version", loaded.Problem);
        Assert.Throws<IOException>(() => store.Put(Tkl, "4.16.8", DefaultProfiles.Key.S, SCal));
        Assert.Equal(newer, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".corrupt"));
    }
}
