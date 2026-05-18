using ApexMapper.Core.Keys;

namespace ApexMapper.Input.Abstractions.Hid;

public sealed class HidReportParser
{
    private readonly HidReportField[] _fields;

    public HidReportParser(IReadOnlyList<HidReportField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var copy = new HidReportField[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            copy[i] = fields[i];
        }
        _fields = copy;
    }

    public void ParseInto(ReadOnlySpan<byte> report, KeyStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

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

            var end = field.ByteOffset + byteWidth;
            if ((uint)field.ByteOffset >= (uint)report.Length || end > report.Length)
            {
                continue;
            }

            float raw = field.BitWidth == 8
                ? report[field.ByteOffset]
                : (ushort)(report[field.ByteOffset] | (report[field.ByteOffset + 1] << 8));

            var normalized = field.Curve.Normalize(raw);
            store.Set(field.Key, normalized, KeyProvenance.Analog);
        }
    }
}
