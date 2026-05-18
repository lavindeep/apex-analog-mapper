using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Input.Abstractions.Hid;
using ApexMapper.Persistence.Json;

namespace ApexMapper.Input.Abstractions.Tests.Adapters;

public class DeviceAdapterStoreTests
{
    private const string ApexProResource =
        "ApexMapper.Input.Abstractions.adapters.steelseries-apex-pro-v2.json";

    private static DeviceAdapterDescriptor MakeDescriptor(IReadOnlyList<KeyMapEntry>? keyMap = null) => new(
        SchemaVersion: "1",
        Id: "vendor.device.v1",
        DisplayName: "Test Device",
        Match: new DeviceMatch(
            VendorId: 0x1038,
            ProductId: 0x161C,
            UsagePage: 0xFFC0,
            ProductRegex: null,
            ManufacturerRegex: null,
            FirmwareVersion: null),
        InterfaceSelector: new InterfaceSelector(
            UsagePage: 0xFFC0,
            UsageId: null,
            CollectionPath: null),
        ReportId: 0,
        ReportType: HidReportType.Feature,
        KeyMap: keyMap ?? Array.Empty<KeyMapEntry>(),
        NoiseFloor: 0.02f,
        RestWindow: 0.05f,
        ProbeHandshake: null,
        Capabilities: new AdapterCapabilities(Analog: true, PerKeyTravel: true));

    [Fact]
    public void LoadEmbedded_returns_apex_pro_descriptor_with_expected_basics()
    {
        var d = DeviceAdapterStore.LoadEmbedded(ApexProResource);

        d.Id.Should().Be("steelseries.apex-pro.v2");
        d.Match.VendorId.Should().Be(0x1038);
        d.Match.ProductId.Should().Be(0x161C);
        d.KeyMap.Should().BeEmpty();
        d.Capabilities.Analog.Should().BeTrue();
    }

    [Fact]
    public void LoadEmbedded_throws_when_resource_missing()
    {
        var act = () => DeviceAdapterStore.LoadEmbedded("ApexMapper.Input.Abstractions.adapters.does-not-exist.json");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void LoadFromFile_round_trips_a_temp_json_file()
    {
        var original = MakeDescriptor(new[]
        {
            new KeyMapEntry(0x001E, 4, 8, NormalizationKind.Linear, 0, 255),
        });

        var path = Path.Combine(Path.GetTempPath(), $"apex-adapter-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerialization.Serialize(original));
            var loaded = DeviceAdapterStore.LoadFromFile(path);
            loaded.Should().BeEquivalentTo(original);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_throws_NotSupportedException_on_schema_version_mismatch()
    {
        var json = """
        {
          "schema_version": "999",
          "id": "vendor.x",
          "display_name": "X",
          "match": { "vendor_id": 1, "product_id": 2 },
          "interface_selector": {},
          "report_id": 0,
          "report_type": "feature",
          "key_map": [],
          "noise_floor": 0.02,
          "rest_window": 0.05,
          "capabilities": { "analog": true, "per_key_travel": true }
        }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"apex-adapter-bad-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var act = () => DeviceAdapterStore.LoadFromFile(path);
            act.Should().Throw<NotSupportedException>();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_throws_InvalidDataException_on_duplicate_scan_code()
    {
        var dup = MakeDescriptor(new[]
        {
            new KeyMapEntry(0x001E, 4, 8, NormalizationKind.Linear, 0, 255),
            new KeyMapEntry(0x001E, 5, 8, NormalizationKind.Linear, 0, 255),
        });

        var path = Path.Combine(Path.GetTempPath(), $"apex-adapter-dup-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerialization.Serialize(dup));
            var act = () => DeviceAdapterStore.LoadFromFile(path);
            act.Should().Throw<InvalidDataException>();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_throws_InvalidDataException_when_vendor_or_product_id_invalid()
    {
        var bad = MakeDescriptor() with
        {
            Match = new DeviceMatch(0, 0x161C, null, null, null, null),
        };
        var path = Path.Combine(Path.GetTempPath(), $"apex-adapter-vid-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerialization.Serialize(bad));
            var act = () => DeviceAdapterStore.LoadFromFile(path);
            act.Should().Throw<InvalidDataException>();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ToFields_produces_one_field_per_key_map_entry_with_default_curve()
    {
        var d = MakeDescriptor(new[]
        {
            new KeyMapEntry(0x001E, ByteOffset: 4, BitWidth: 8, Normalization: NormalizationKind.Linear, RawMin: 0, RawMax: 255),
            new KeyMapEntry(0x0020, ByteOffset: 5, BitWidth: 8, Normalization: NormalizationKind.Inverted, RawMin: 255, RawMax: 0),
        });

        var fields = DeviceAdapterStore.ToFields(d);

        fields.Should().HaveCount(2);

        var f0 = fields[0];
        f0.Key.Should().Be(KeyId.FromScanCode(0x001E));
        f0.ByteOffset.Should().Be(4);
        f0.BitWidth.Should().Be(8);
        f0.Curve.Rest.Should().Be(0f);
        f0.Curve.Max.Should().Be(255f);
        f0.Curve.Kind.Should().Be(NormalizationKind.Linear);
        // NoiseBand = NoiseFloor * (Max - Rest) = 0.02 * 255 = 5.1
        f0.Curve.NoiseBand.Should().BeApproximately(0.02f * 255f, 1e-4f);

        var f1 = fields[1];
        f1.Key.Should().Be(KeyId.FromScanCode(0x0020));
        f1.ByteOffset.Should().Be(5);
        f1.Curve.Rest.Should().Be(255f);
        f1.Curve.Max.Should().Be(0f);
        f1.Curve.Kind.Should().Be(NormalizationKind.Inverted);
        // Use absolute span for noise band
        f1.Curve.NoiseBand.Should().BeApproximately(0.02f * 255f, 1e-4f);
    }

    [Fact]
    public void ToFields_honors_per_key_calibration_override()
    {
        var d = MakeDescriptor(new[]
        {
            new KeyMapEntry(0x001E, 4, 8, NormalizationKind.Linear, 0, 255),
            new KeyMapEntry(0x0020, 5, 8, NormalizationKind.Linear, 0, 255),
        });
        var customCurve = new CalibrationCurve(Rest: 10f, Max: 200f, NoiseBand: 3.5f, Kind: NormalizationKind.Inverted);
        var overrides = new Dictionary<KeyId, CalibrationCurve>
        {
            [KeyId.FromScanCode(0x001E)] = customCurve,
        };

        var fields = DeviceAdapterStore.ToFields(d, overrides);

        fields.Should().HaveCount(2);
        fields[0].Curve.Should().Be(customCurve);
        // Second key should still use default
        fields[1].Curve.Rest.Should().Be(0f);
        fields[1].Curve.Max.Should().Be(255f);
        fields[1].Curve.Kind.Should().Be(NormalizationKind.Linear);
    }

    [Fact]
    public void End_to_end_apex_pro_with_empty_key_map_leaves_store_at_rest()
    {
        var d = DeviceAdapterStore.LoadEmbedded(ApexProResource);
        var fields = DeviceAdapterStore.ToFields(d, calibrationOverrides: null);

        var probe = KeyId.FromScanCode(0x001E);
        var store = new KeyStateStore(new KeyIndex(new[] { probe }));
        var parser = new HidReportParser(fields);

        Span<byte> report = stackalloc byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        parser.ParseInto(report, store);

        store.Get(probe).Should().Be(KeyState.Rest);
    }
}
