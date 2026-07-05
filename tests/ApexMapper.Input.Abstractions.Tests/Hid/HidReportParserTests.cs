using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Input.Abstractions.Hid;

namespace ApexMapper.Input.Abstractions.Tests.Hid;

public class HidReportParserTests
{
    private static KeyId K(ushort s) => KeyId.FromScanCode(s);

    private static KeyStateStore MakeStore(params KeyId[] keys) => new(new KeyIndex(keys));

    private static CalibrationCurve Linear8 => new(0f, 255f, 2f, NormalizationKind.Linear);

    [Fact]
    public void Single_8bit_field_writes_normalized_value()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var fields = new[] { new HidReportField(key, ByteOffset: 0, BitWidth: 8, Curve: Linear8) };
        var parser = new HidReportParser(fields);

        Span<byte> report = stackalloc byte[] { 0x80 };
        parser.ParseInto(report, store);

        store.Get(key).Value.Should().BeApproximately(128f / 255f, 1e-4f);
    }

    [Fact]
    public void Multiple_fields_mixed_widths_populate_both_keys()
    {
        var k8 = K(0x11);
        var k16 = K(0x1E);
        var store = MakeStore(k8, k16);

        var curve16 = new CalibrationCurve(0f, 65535f, 4f, NormalizationKind.Linear);
        var fields = new[]
        {
            new HidReportField(k8, ByteOffset: 1, BitWidth: 8, Curve: Linear8),
            new HidReportField(k16, ByteOffset: 3, BitWidth: 16, Curve: curve16),
        };
        var parser = new HidReportParser(fields);

        // Bytes:                        0     1     2     3     4
        // For 16-bit little-endian, raw = 0xFF | (0x7F << 8) = 0x7FFF = 32767
        Span<byte> report = stackalloc byte[] { 0x00, 0x80, 0x00, 0xFF, 0x7F };
        parser.ParseInto(report, store);

        store.Get(k8).Value.Should().BeApproximately(128f / 255f, 1e-4f);
        store.Get(k16).Value.Should().BeApproximately(32767f / 65535f, 1e-4f);
    }

    [Fact]
    public void Parsed_values_have_Analog_provenance()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var fields = new[] { new HidReportField(key, 0, 8, Linear8) };
        var parser = new HidReportParser(fields);

        Span<byte> report = stackalloc byte[] { 0x80 };
        parser.ParseInto(report, store);

        store.Get(key).Source.Should().Be(KeyProvenance.Analog);
    }

    [Fact]
    public void Truncated_report_skips_field_gracefully()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        // Field expects byte at offset 3, but report only has 1 byte.
        var fields = new[] { new HidReportField(key, ByteOffset: 3, BitWidth: 8, Curve: Linear8) };
        var parser = new HidReportParser(fields);

        Span<byte> report = stackalloc byte[] { 0x80 };
        parser.ParseInto(report, store);

        // Untouched -> Rest (0f, Digital).
        store.Get(key).Should().Be(KeyState.Rest);
    }

    [Fact]
    public void Truncated_16bit_field_skipped_when_only_one_byte_available()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var fields = new[] { new HidReportField(key, ByteOffset: 0, BitWidth: 16, Curve: Linear8) };
        var parser = new HidReportParser(fields);

        // 16-bit field needs 2 bytes from offset 0, but we only have 1.
        Span<byte> report = stackalloc byte[] { 0xFF };
        parser.ParseInto(report, store);

        store.Get(key).Should().Be(KeyState.Rest);
    }

    [Fact]
    public void Unsupported_bit_width_throws_NotSupportedException()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var fields = new[] { new HidReportField(key, 0, BitWidth: 12, Curve: Linear8) };
        var parser = new HidReportParser(fields);

        var report = new byte[] { 0x80, 0x00 };
        var act = () => parser.ParseInto(report, store);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Empty_field_list_is_noop()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var parser = new HidReportParser(Array.Empty<HidReportField>());

        Span<byte> report = stackalloc byte[] { 0x12, 0x34, 0x56 };
        parser.ParseInto(report, store);

        store.Get(key).Should().Be(KeyState.Rest);
    }

    [Fact]
    public void Reset_round_trip_zeroes_keys_and_parse_repopulates()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var fields = new[] { new HidReportField(key, 0, 8, Linear8) };
        var parser = new HidReportParser(fields);

        Span<byte> report1 = stackalloc byte[] { 0xFF };
        parser.ParseInto(report1, store);
        store.Get(key).Value.Should().Be(1f);

        store.Reset();
        store.Get(key).Value.Should().Be(0f);

        Span<byte> report2 = stackalloc byte[] { 0x80 };
        parser.ParseInto(report2, store);
        store.Get(key).Value.Should().BeApproximately(128f / 255f, 1e-4f);
    }

    [Fact]
    public void Gated_key_is_left_at_zero_by_analog_reports()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var fields = new[] { new HidReportField(key, ByteOffset: 0, BitWidth: 8, Curve: Linear8) };
        var parser = new HidReportParser(fields);

        store.Set(key, 0.9f, KeyProvenance.Analog);
        store.GateHeldKeys();

        // Analog reports must not bypass the held-key gate.
        Span<byte> report = stackalloc byte[] { 0xFF };
        parser.ParseInto(report, store);

        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeTrue();
    }

    [Fact]
    public void Numbered_report_offsets_payload_by_the_report_id_byte()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        // Field payload offset is authored relative to the report data (0); on a
        // numbered device the OS prepends the report id at buffer[0].
        var fields = new[] { new HidReportField(key, ByteOffset: 0, BitWidth: 8, Curve: Linear8) };
        var parser = new HidReportParser(fields, reportId: 0x05);

        Span<byte> report = stackalloc byte[] { 0x05, 0x80 };
        parser.ParseInto(report, store);

        store.Get(key).Value.Should().BeApproximately(128f / 255f, 1e-4f);
    }

    [Fact]
    public void Unnumbered_report_reads_payload_from_offset_zero()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var fields = new[] { new HidReportField(key, ByteOffset: 0, BitWidth: 8, Curve: Linear8) };
        var parser = new HidReportParser(fields); // reportId defaults to 0

        Span<byte> report = stackalloc byte[] { 0x80 };
        parser.ParseInto(report, store);

        store.Get(key).Value.Should().BeApproximately(128f / 255f, 1e-4f);
    }

    [Fact]
    public void Numbered_report_with_mismatched_id_is_ignored()
    {
        var key = K(0x11);
        var store = MakeStore(key);
        var fields = new[] { new HidReportField(key, ByteOffset: 0, BitWidth: 8, Curve: Linear8) };
        var parser = new HidReportParser(fields, reportId: 0x05);

        // A report for a different id must not write our field.
        Span<byte> report = stackalloc byte[] { 0x07, 0xFF };
        parser.ParseInto(report, store);

        store.Get(key).Should().Be(KeyState.Rest);
    }

    [Fact]
    public void Numbered_report_16bit_field_reads_shifted_little_endian_pair()
    {
        var key = K(0x1E);
        var store = MakeStore(key);
        var curve16 = new CalibrationCurve(0f, 65535f, 4f, NormalizationKind.Linear);
        var fields = new[] { new HidReportField(key, ByteOffset: 0, BitWidth: 16, Curve: curve16) };
        var parser = new HidReportParser(fields, reportId: 0x0A);

        // buffer[0]=id, buffer[1..2]=payload = 0x7FFF little-endian.
        Span<byte> report = stackalloc byte[] { 0x0A, 0xFF, 0x7F };
        parser.ParseInto(report, store);

        store.Get(key).Value.Should().BeApproximately(32767f / 65535f, 1e-4f);
    }

    [Fact]
    public void Ctor_throws_when_fields_null()
    {
        var act = () => new HidReportParser(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ParseInto_throws_when_store_null()
    {
        var parser = new HidReportParser(Array.Empty<HidReportField>());
        // ReadOnlySpan can't cross lambda boundary, so call directly inside Action.
        Action act = () => parser.ParseInto(ReadOnlySpan<byte>.Empty, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
