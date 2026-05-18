using ApexMapper.Input.Abstractions.Hid;

namespace ApexMapper.Input.Abstractions.Tests.Hid;

/// <summary>
/// Cross-platform unit tests for <see cref="HidReportDescriptorParser"/>.
/// Descriptors are built inline as <c>byte[]</c> using the USB HID class
/// spec §6.2.2 short-item format:
///   Item-prefix byte: high 4 bits = tag, mid 2 bits = type, low 2 bits = size.
///   size: 0 → 0 bytes, 1 → 1 byte, 2 → 2 bytes, 3 → 4 bytes.
/// </summary>
public class HidReportDescriptorParserTests
{
    // --- Helpers --------------------------------------------------------

    private const byte Type_Main = 0b00;
    private const byte Type_Global = 0b01;
    private const byte Type_Local = 0b10;

    private const byte MainTag_Input = 0x8;
    private const byte MainTag_Output = 0x9;
    private const byte MainTag_Feature = 0xB;
    private const byte MainTag_Collection = 0xA;
    private const byte MainTag_EndCollection = 0xC;

    private const byte GlobalTag_UsagePage = 0x0;
    private const byte GlobalTag_ReportSize = 0x7;
    private const byte GlobalTag_ReportId = 0x8;
    private const byte GlobalTag_ReportCount = 0x9;

    private const byte LocalTag_Usage = 0x0;

    private static byte Prefix(byte tag, byte type, byte size) =>
        (byte)(((tag & 0xF) << 4) | ((type & 0x3) << 2) | (size & 0x3));

    // --- 1) Minimal Input-only gamepad ---------------------------------

    [Fact]
    public void Parses_minimal_gamepad_input_descriptor()
    {
        // Usage Page (Generic Desktop) 05 01
        // Usage (Joystick)             09 04
        // Collection (Application)     A1 01
        //   Usage (X)                  09 30
        //   Usage (Y)                  09 31
        //   Report Size (8)            75 08
        //   Report Count (2)           95 02
        //   Input (Data,Var,Abs)       81 02
        // End Collection               C0
        var descriptor = new byte[]
        {
            Prefix(GlobalTag_UsagePage, Type_Global, 1), 0x01,
            Prefix(LocalTag_Usage, Type_Local, 1), 0x04,
            Prefix(MainTag_Collection, Type_Main, 1), 0x01,
            Prefix(LocalTag_Usage, Type_Local, 1), 0x30,
            Prefix(LocalTag_Usage, Type_Local, 1), 0x31,
            Prefix(GlobalTag_ReportSize, Type_Global, 1), 0x08,
            Prefix(GlobalTag_ReportCount, Type_Global, 1), 0x02,
            Prefix(MainTag_Input, Type_Main, 1), 0x02,
            Prefix(MainTag_EndCollection, Type_Main, 0),
        };

        var result = HidReportDescriptorParser.Parse(descriptor);

        result.MalformedItemCount.Should().Be(0);
        result.Fields.Should().HaveCount(1);
        var field = result.Fields[0];
        field.AccessMode.Should().Be(HidAccessMode.Input);
        field.ReportId.Should().Be((byte)0);
        field.BitOffset.Should().Be(0);
        field.BitSize.Should().Be(16);
        field.UsagePage.Should().Be((ushort)0x0001);
    }

    // --- 2) Explicit Report IDs and per-ID bit offset tracking ---------

    [Fact]
    public void Tracks_bit_offset_per_report_id()
    {
        // Report ID 1: one Input field, 8 bits.
        // Report ID 2: one Feature field, 16 bits.
        // When the Report ID changes the BitOffset must reset to 0.
        var descriptor = new byte[]
        {
            Prefix(GlobalTag_UsagePage, Type_Global, 1), 0x01,
            Prefix(GlobalTag_ReportId, Type_Global, 1), 0x01,
            Prefix(GlobalTag_ReportSize, Type_Global, 1), 0x08,
            Prefix(GlobalTag_ReportCount, Type_Global, 1), 0x01,
            Prefix(MainTag_Input, Type_Main, 1), 0x02,
            Prefix(GlobalTag_ReportId, Type_Global, 1), 0x02,
            Prefix(GlobalTag_ReportSize, Type_Global, 1), 0x08,
            Prefix(GlobalTag_ReportCount, Type_Global, 1), 0x02,
            Prefix(MainTag_Feature, Type_Main, 1), 0x02,
        };

        var result = HidReportDescriptorParser.Parse(descriptor);

        result.MalformedItemCount.Should().Be(0);
        result.Fields.Should().HaveCount(2);

        result.Fields[0].ReportId.Should().Be((byte)1);
        result.Fields[0].AccessMode.Should().Be(HidAccessMode.Input);
        result.Fields[0].BitOffset.Should().Be(0);
        result.Fields[0].BitSize.Should().Be(8);

        result.Fields[1].ReportId.Should().Be((byte)2);
        result.Fields[1].AccessMode.Should().Be(HidAccessMode.Feature);
        result.Fields[1].BitOffset.Should().Be(0);
        result.Fields[1].BitSize.Should().Be(16);
    }

    [Fact]
    public void Accumulates_bit_offset_within_same_report_id()
    {
        // Report ID 1, two Input items: 8 bits then 16 bits.
        // Second item BitOffset must be 8 (still the same report).
        var descriptor = new byte[]
        {
            Prefix(GlobalTag_ReportId, Type_Global, 1), 0x01,
            Prefix(GlobalTag_ReportSize, Type_Global, 1), 0x08,
            Prefix(GlobalTag_ReportCount, Type_Global, 1), 0x01,
            Prefix(MainTag_Input, Type_Main, 1), 0x02,
            Prefix(GlobalTag_ReportSize, Type_Global, 1), 0x08,
            Prefix(GlobalTag_ReportCount, Type_Global, 1), 0x02,
            Prefix(MainTag_Input, Type_Main, 1), 0x02,
        };

        var result = HidReportDescriptorParser.Parse(descriptor);

        result.Fields.Should().HaveCount(2);
        result.Fields[0].BitOffset.Should().Be(0);
        result.Fields[0].BitSize.Should().Be(8);
        result.Fields[1].BitOffset.Should().Be(8);
        result.Fields[1].BitSize.Should().Be(16);
    }

    // --- 3) Nested collections ------------------------------------------

    [Fact]
    public void Handles_nested_collections_without_corrupting_state()
    {
        // Outer application collection with an inner logical collection
        // containing one Input item. Verifies we walk Collection/EndCollection
        // pairs without dropping the item or mis-attributing usage page.
        var descriptor = new byte[]
        {
            Prefix(GlobalTag_UsagePage, Type_Global, 1), 0x01,
            Prefix(LocalTag_Usage, Type_Local, 1), 0x05, // Game Pad
            Prefix(MainTag_Collection, Type_Main, 1), 0x01, // Application
            Prefix(MainTag_Collection, Type_Main, 1), 0x02, // Logical
            Prefix(GlobalTag_ReportSize, Type_Global, 1), 0x08,
            Prefix(GlobalTag_ReportCount, Type_Global, 1), 0x01,
            Prefix(MainTag_Input, Type_Main, 1), 0x02,
            Prefix(MainTag_EndCollection, Type_Main, 0),
            Prefix(MainTag_EndCollection, Type_Main, 0),
        };

        var result = HidReportDescriptorParser.Parse(descriptor);

        result.MalformedItemCount.Should().Be(0);
        result.Fields.Should().ContainSingle();
        result.Fields[0].UsagePage.Should().Be((ushort)0x0001);
    }

    // --- 4) Malformed input ---------------------------------------------

    [Fact]
    public void Truncated_short_item_does_not_throw()
    {
        // size=2 (two data bytes) but only one byte follows. Parser must stop
        // gracefully and surface the truncation via MalformedItemCount.
        var descriptor = new byte[]
        {
            Prefix(GlobalTag_UsagePage, Type_Global, 2), 0x01,
        };

        var act = () => HidReportDescriptorParser.Parse(descriptor);

        act.Should().NotThrow();
        var result = HidReportDescriptorParser.Parse(descriptor);
        result.MalformedItemCount.Should().BeGreaterThan(0);
        result.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Long_item_prefix_is_skipped_gracefully()
    {
        // 0xFE is the long-item prefix; the spec reserves it. We don't support
        // long items but we must not crash if one appears.
        var descriptor = new byte[]
        {
            0xFE, 0x02, 0x00, 0xAA, 0xBB, // long item: 2 bytes of data, tag 0, then 2 data bytes
            Prefix(GlobalTag_ReportSize, Type_Global, 1), 0x08,
            Prefix(GlobalTag_ReportCount, Type_Global, 1), 0x01,
            Prefix(MainTag_Input, Type_Main, 1), 0x02,
        };

        var result = HidReportDescriptorParser.Parse(descriptor);

        // Long item is skipped; the trailing Input item is still emitted.
        result.Fields.Should().ContainSingle();
        result.Fields[0].AccessMode.Should().Be(HidAccessMode.Input);
    }

    // --- 5) Output and Feature tagging ----------------------------------

    [Fact]
    public void Tags_input_output_feature_separately()
    {
        var descriptor = new byte[]
        {
            Prefix(GlobalTag_ReportSize, Type_Global, 1), 0x08,
            Prefix(GlobalTag_ReportCount, Type_Global, 1), 0x01,
            Prefix(MainTag_Input, Type_Main, 1), 0x02,
            Prefix(MainTag_Output, Type_Main, 1), 0x02,
            Prefix(MainTag_Feature, Type_Main, 1), 0x02,
        };

        var result = HidReportDescriptorParser.Parse(descriptor);

        result.Fields.Should().HaveCount(3);
        result.Fields.Select(f => f.AccessMode).Should().Equal(
            HidAccessMode.Input, HidAccessMode.Output, HidAccessMode.Feature);
    }

    [Fact]
    public void Parse_throws_on_null_descriptor()
    {
        var act = () => HidReportDescriptorParser.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
