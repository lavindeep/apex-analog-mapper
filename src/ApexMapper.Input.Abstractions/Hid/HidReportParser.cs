using ApexMapper.Core.Keys;

namespace ApexMapper.Input.Abstractions.Hid;

public sealed class HidReportParser
{
    private readonly HidReportField[] _fields;
    private readonly byte _reportId;
    private readonly int _payloadOffset;

    /// <param name="reportId">
    /// The HID Report ID this parser's fields belong to. Zero means the device
    /// emits unnumbered reports (no leading id byte). When non-zero, the first
    /// report byte is the id: field offsets are shifted past it, and reports
    /// carrying a different id are ignored.
    /// </param>
    public HidReportParser(IReadOnlyList<HidReportField> fields, byte reportId = 0)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var copy = new HidReportField[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            copy[i] = fields[i];
        }
        _fields = copy;
        _reportId = reportId;
        _payloadOffset = reportId != 0 ? 1 : 0;
    }

    public void ParseInto(ReadOnlySpan<byte> report, KeyStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        // On a numbered device the leading byte is the report id; field offsets
        // are payload-relative, so shift them past it. A report for a different
        // id is not ours — leave the store untouched rather than misread it.
        if (_reportId != 0 && (report.Length < 1 || report[0] != _reportId))
        {
            return;
        }

        var fields = _fields;
        for (var i = 0; i < fields.Length; i++)
        {
            ref readonly var field = ref fields[i];
            var byteWidth = field.BitWidth switch
            {
                8 => 1,
                16 => 2,
                _ => throw new NotSupportedException(
                    $"HidReportField BitWidth {field.BitWidth} is not supported; expected 8 or 16."),
            };

            var byteOffset = field.ByteOffset + _payloadOffset;
            var end = byteOffset + byteWidth;
            if ((uint)byteOffset >= (uint)report.Length || end > report.Length)
            {
                continue;
            }

            float raw = field.BitWidth == 8
                ? report[byteOffset]
                : (ushort)(report[byteOffset] | (report[byteOffset + 1] << 8));

            var normalized = field.Curve.Normalize(raw);
            store.Set(field.Key, normalized, KeyProvenance.Analog);
        }
    }
}
