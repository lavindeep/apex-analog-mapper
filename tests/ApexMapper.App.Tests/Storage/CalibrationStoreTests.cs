using System.IO;
using ApexMapper.App.Storage;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Profiles;
using Xunit;

namespace ApexMapper.App.Tests.Storage;

public class CalibrationStoreTests
{
    private static readonly Guid Tkl = new("27373de1-4206-11f1-b9e4-14ac60fcc13e");
    private static readonly Guid Other = new("11111111-2222-3333-4444-555555555555");
    private static readonly KeyCalibration WCal = KeyCalibration.Create(1900, 4095, 40, 16);
    private static readonly KeyCalibration SCal = KeyCalibration.Create(2100, 300, 45, 30);

    [Fact]
    public void Nothing_saved_is_empty_without_a_problem()
    {
        using var dir = new TempDirectory();

        var loaded = new CalibrationStore(dir.Path).Load(Tkl, "4.16.8");

        Assert.Empty(loaded.Keys);
        Assert.Empty(loaded.FromOtherFirmware);
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
    public void An_invalid_entry_is_left_out_and_reported_and_the_rest_load()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File(Tkl.ToString("D") + ".json"), """
            { "version": 1, "payload": { "keys": {
              "0x11": { "rest": 1900, "full_press": 4095, "noise_band": 40, "sensor_index": 16, "firmware": "4.16.8" },
              "0x1F": { "rest": 2100, "full_press": 2150, "noise_band": 40, "sensor_index": 99, "firmware": "4.16.8" }
            } } }
            """);

        var loaded = new CalibrationStore(dir.Path).Load(Tkl, "4.16.8");

        Assert.Equal([DefaultProfiles.Key.W], loaded.Keys.Keys);
        Assert.Contains("must be calibrated again", loaded.Problem);
    }

    [Fact]
    public void Put_refuses_an_invalid_calibration_and_a_file_it_cannot_read()
    {
        using var dir = new TempDirectory();
        var store = new CalibrationStore(dir.Path);
        store.Put(Tkl, "4.16.8", DefaultProfiles.Key.W, WCal);

        Assert.Throws<ArgumentException>(() => store.Put(Tkl, "4.16.8", DefaultProfiles.Key.S, SCal with { SensorIndex = 99 }));

        // Held open without sharing, as another program might: the stored W must survive.
        var path = dir.File(Tkl.ToString("D") + ".json");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Contains("could not be opened", store.Load(Tkl, "4.16.8").Problem);
            Assert.Throws<IOException>(() => store.Put(Tkl, "4.16.8", DefaultProfiles.Key.S, SCal));
        }
        Assert.Equal([DefaultProfiles.Key.W], store.Load(Tkl, "4.16.8").Keys.Keys);
    }
}
