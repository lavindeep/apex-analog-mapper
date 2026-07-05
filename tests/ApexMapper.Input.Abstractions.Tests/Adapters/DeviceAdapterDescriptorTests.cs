using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Persistence.Json;

namespace ApexMapper.Input.Abstractions.Tests.Adapters;

public class DeviceAdapterDescriptorTests
{
    private static DeviceAdapterDescriptor MakeDescriptor() => new(
        SchemaVersion: "1",
        Id: "vendor.device.v1",
        DisplayName: "Test Device",
        Match: new DeviceMatch(
            VendorId: 0x1038,
            ProductId: 0x161C,
            UsagePage: 0xFFC0,
            ProductRegex: "Apex.*",
            ManufacturerRegex: "SteelSeries",
            FirmwareVersion: "1.42.0"),
        InterfaceSelector: new InterfaceSelector(
            UsagePage: 0xFFC0,
            UsageId: 0x0001,
            CollectionPath: "0001"),
        ReportId: 0x07,
        ReportType: HidReportType.Feature,
        KeyMap: new[]
        {
            new KeyMapEntry(
                ScanCode: 0x001E,
                ByteOffset: 4,
                BitWidth: 8,
                Normalization: NormalizationKind.Inverted,
                // Ascending authoring: raw_max is the physical-rest reading for
                // inverted travel, raw_min is full press.
                RawMin: 0,
                RawMax: 255),
        },
        NoiseFloor: 0.02f,
        RestWindow: 0.05f,
        ProbeHandshake: new ProbeHandshake(
            FeatureReportRequest: new byte[] { 0x01, 0x02, 0x03 },
            ResponseTimeoutMs: 250),
        Capabilities: new AdapterCapabilities(Analog: true, PerKeyTravel: true));

    [Fact]
    public void Round_trips_through_JsonSerialization()
    {
        var original = MakeDescriptor();
        var json = JsonSerialization.Serialize(original);
        var back = JsonSerialization.Deserialize<DeviceAdapterDescriptor>(json)!;
        back.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Serializes_with_snake_case_property_names()
    {
        var json = JsonSerialization.Serialize(MakeDescriptor());

        json.Should().Contain("\"schema_version\"");
        json.Should().Contain("\"display_name\"");
        json.Should().Contain("\"vendor_id\"");
        json.Should().Contain("\"product_id\"");
        json.Should().Contain("\"interface_selector\"");
        json.Should().Contain("\"key_map\"");
        json.Should().Contain("\"report_type\"");
        json.Should().Contain("\"report_id\"");
        json.Should().Contain("\"raw_min\"");
        json.Should().Contain("\"raw_max\"");
        json.Should().Contain("\"byte_offset\"");
        json.Should().Contain("\"bit_width\"");
        json.Should().Contain("\"noise_floor\"");
        json.Should().Contain("\"rest_window\"");
        json.Should().Contain("\"probe_handshake\"");
        json.Should().Contain("\"feature_report_request\"");
        json.Should().Contain("\"response_timeout_ms\"");
        json.Should().Contain("\"per_key_travel\"");
    }

    [Fact]
    public void Enums_serialize_as_snake_case_strings()
    {
        var json = JsonSerialization.Serialize(MakeDescriptor());

        json.Should().Contain("\"feature\"");
        json.Should().Contain("\"inverted\"");
    }

    [Fact]
    public void Null_fields_are_dropped_from_serialized_output()
    {
        var d = MakeDescriptor() with
        {
            Match = new DeviceMatch(
                VendorId: 0x1038,
                ProductId: 0x161C,
                UsagePage: null,
                ProductRegex: null,
                ManufacturerRegex: null,
                FirmwareVersion: null),
            ProbeHandshake = null,
        };

        var json = JsonSerialization.Serialize(d);

        json.Should().NotContain("\"product_regex\"");
        json.Should().NotContain("\"manufacturer_regex\"");
        json.Should().NotContain("\"firmware_version\"");
        json.Should().NotContain("\"probe_handshake\"");
        // usage_page on Match is null but InterfaceSelector still has one;
        // make sure dropping is local to the Match record.
        json.Should().Contain("\"interface_selector\"");
    }
}
