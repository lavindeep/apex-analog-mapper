using System.Buffers.Binary;
using System.Text;

namespace ApexMapper.Core.Sensors;

/// <summary>
/// Pure parsing of the Apex Pro vendor replies. Verified on firmware 4.9.1 with the
/// captures in docs/design/fixtures.
///
/// Reply layout for a group read (65 bytes): byte 0 is the report id and must be 0;
/// bytes 1..28 are fourteen raw uint16 little-endian samples; bytes 29..56 are the
/// firmware's filtered copies; bytes 57..64 must be zero. Samples are 12-bit.
/// Replies carry no command echo, so callers must serialise exchanges.
/// </summary>
public static class SensorProtocol
{
    public const int ReportLength = 65;
    public const int SensorsPerGroup = 14;
    public const int SensorCount = SensorRequest.GroupCount * SensorsPerGroup;
    public const int MaxCount = 4095;

    /// <summary>A slot without a physical key reads under this at rest (4 to 9 seen; keys read about 850).</summary>
    public const int AbsentSensorCeiling = 50;

    private const int RawOffset = 1;
    private const int FilteredOffset = 29;
    private const int PaddingOffset = 57;

    /// <summary>Parses a firmware reply. Returns an error, or null with the version set.</summary>
    public static string? ParseFirmware(ReadOnlySpan<byte> reply, out string version)
    {
        version = string.Empty;
        if (reply.Length != ReportLength)
        {
            return $"Expected a {ReportLength}-byte reply, got {reply.Length}.";
        }
        if (reply[0] != 0)
        {
            return "Reply report id was not zero.";
        }
        var text = reply[1..];
        var end = text.IndexOf((byte)0);
        if (end < 0)
        {
            end = text.Length;
        }
        if (end == 0)
        {
            return "Firmware reply was empty.";
        }
        for (var i = 0; i < end; i++)
        {
            if (text[i] is < 0x20 or > 0x7E)
            {
                return "Firmware reply was not printable ASCII.";
            }
        }
        for (var i = end; i < text.Length; i++)
        {
            if (text[i] != 0)
            {
                return "Firmware reply had bytes after the terminator.";
            }
        }
        version = Encoding.ASCII.GetString(text[..end]);
        return LooksLikeVersion(version) ? null : $"Firmware reply '{version}' does not look like a version.";
    }

    /// <summary>Digits and dots, at least one dot, nothing else, as in 4.9.1.</summary>
    public static bool LooksLikeVersion(string text) =>
        text.Length is >= 3 and <= 16 && text.Contains('.') && text.All(c => char.IsAsciiDigit(c) || c == '.') && !text.StartsWith('.') && !text.EndsWith('.');

    /// <summary>
    /// Parses a group reply into fourteen raw and fourteen filtered samples. Returns an
    /// error, or null on success. Structural only; see the plausibility checks below.
    /// </summary>
    public static string? ParseGroup(ReadOnlySpan<byte> reply, Span<ushort> raw, Span<ushort> filtered)
    {
        if (raw.Length < SensorsPerGroup || filtered.Length < SensorsPerGroup)
        {
            throw new ArgumentException("Output spans must hold fourteen samples.");
        }
        if (reply.Length != ReportLength)
        {
            return $"Expected a {ReportLength}-byte reply, got {reply.Length}.";
        }
        if (reply[0] != 0)
        {
            return "Reply report id was not zero.";
        }
        for (var i = PaddingOffset; i < ReportLength; i++)
        {
            if (reply[i] != 0)
            {
                return "Reply padding was not zero.";
            }
        }
        for (var i = 0; i < SensorsPerGroup; i++)
        {
            var r = BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(RawOffset + 2 * i, 2));
            var f = BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(FilteredOffset + 2 * i, 2));
            if (r > MaxCount || f > MaxCount)
            {
                return "A sample exceeded the 12-bit range.";
            }
            raw[i] = r;
            filtered[i] = f;
        }
        return null;
    }

    /// <summary>A group whose samples are all zero or all identical is not sensor data.</summary>
    public static bool IsPlausibleGroup(ReadOnlySpan<ushort> raw)
    {
        var allZero = true;
        var allSame = true;
        for (var i = 0; i < raw.Length; i++)
        {
            allZero &= raw[i] == 0;
            allSame &= raw[i] == raw[0];
        }
        return !allZero && !allSame;
    }

    public static bool HasSensor(ushort restReading) => restReading > AbsentSensorCeiling;
}
