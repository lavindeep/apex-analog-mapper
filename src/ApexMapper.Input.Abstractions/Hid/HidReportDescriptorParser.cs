namespace ApexMapper.Input.Abstractions.Hid;

/// <summary>
/// Pure-function parser for USB HID class spec §6.2.2 short-item report
/// descriptors. Produces a flat list of <see cref="HidReportDescriptorField"/>
/// records — one per Input / Output / Feature Main item — annotated with the
/// active Report ID, Usage Page, and Usage at the time the item was emitted.
/// </summary>
/// <remarks>
/// <para>
/// The parser tracks the standard HID global-item state machine: Usage Page,
/// Report Size, Report Count, and Report ID. Local items (Usage) are
/// consumed by the next Main item and then cleared, per the spec.
/// </para>
/// <para>
/// Bit offsets are cumulative <em>per report</em>: switching the active
/// Report ID resets the offset to zero so that <c>BitOffset</c> matches the
/// position within that report's payload (i.e. after the leading Report ID
/// byte, which is implicit).
/// </para>
/// <para>
/// Malformed input — truncated short items, the reserved long-item prefix —
/// is tolerated. The parser counts malformed items via
/// <see cref="HidReportDescriptorParseResult.MalformedItemCount"/> and
/// continues from the next plausible boundary rather than throwing.
/// </para>
/// </remarks>
public static class HidReportDescriptorParser
{
    private const byte LongItemPrefix = 0xFE;

    /// <summary>Parses <paramref name="descriptor"/> into a flat field list.</summary>
    /// <exception cref="ArgumentNullException">If <paramref name="descriptor"/> is null.</exception>
    public static HidReportDescriptorParseResult Parse(byte[] descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Parse((ReadOnlySpan<byte>)descriptor);
    }

    /// <summary>Span overload for callers that already hold a slice.</summary>
    public static HidReportDescriptorParseResult Parse(ReadOnlySpan<byte> descriptor)
    {
        var fields = new List<HidReportDescriptorField>();
        var bitOffsetsByReportId = new Dictionary<byte, int>();
        var malformed = 0;

        ushort usagePage = 0;
        int reportSize = 0;
        int reportCount = 0;
        byte reportId = 0;
        uint pendingUsage = 0;
        bool hasPendingUsage = false;

        var i = 0;
        while (i < descriptor.Length)
        {
            var prefix = descriptor[i];

            if (prefix == LongItemPrefix)
            {
                // Long item: prefix, bSize, bTag, then bSize data bytes.
                if (i + 2 >= descriptor.Length)
                {
                    malformed++;
                    break;
                }
                int longDataLen = descriptor[i + 1];
                var longTotal = 3 + longDataLen;
                if (i + longTotal > descriptor.Length)
                {
                    malformed++;
                    break;
                }
                i += longTotal;
                continue;
            }

            var size = prefix & 0x3;
            var type = (prefix >> 2) & 0x3;
            var tag = (prefix >> 4) & 0xF;

            var dataLen = size == 3 ? 4 : size;
            if (i + 1 + dataLen > descriptor.Length)
            {
                malformed++;
                break;
            }

            // Read data little-endian into a 32-bit signed/unsigned value.
            uint data = 0;
            for (var b = 0; b < dataLen; b++)
            {
                data |= (uint)descriptor[i + 1 + b] << (8 * b);
            }

            switch (type)
            {
                case 0: // Main
                    HandleMain(
                        tag, data,
                        ref usagePage, ref reportSize, ref reportCount,
                        ref reportId, ref pendingUsage, ref hasPendingUsage,
                        bitOffsetsByReportId, fields);
                    break;
                case 1: // Global
                    HandleGlobal(tag, data, ref usagePage, ref reportSize, ref reportCount, ref reportId);
                    break;
                case 2: // Local
                    HandleLocal(tag, data, ref pendingUsage, ref hasPendingUsage);
                    break;
                default:
                    // Reserved type 3 — skip but count.
                    malformed++;
                    break;
            }

            i += 1 + dataLen;
        }

        return new HidReportDescriptorParseResult(fields, malformed);
    }

    private static void HandleMain(
        int tag, uint data,
        ref ushort usagePage, ref int reportSize, ref int reportCount,
        ref byte reportId, ref uint pendingUsage, ref bool hasPendingUsage,
        Dictionary<byte, int> bitOffsetsByReportId,
        List<HidReportDescriptorField> fields)
    {
        switch (tag)
        {
            case 0x8: // Input
            case 0x9: // Output
            case 0xB: // Feature
                if (reportSize > 0 && reportCount > 0)
                {
                    var mode = tag switch
                    {
                        0x8 => HidAccessMode.Input,
                        0x9 => HidAccessMode.Output,
                        _ => HidAccessMode.Feature,
                    };

                    if (!bitOffsetsByReportId.TryGetValue(reportId, out var offset))
                    {
                        offset = 0;
                    }

                    var bitSize = reportSize * reportCount;
                    fields.Add(new HidReportDescriptorField(
                        ReportId: reportId,
                        AccessMode: mode,
                        BitOffset: offset,
                        BitSize: bitSize,
                        UsagePage: usagePage,
                        Usage: hasPendingUsage ? pendingUsage : 0u));

                    bitOffsetsByReportId[reportId] = offset + bitSize;
                }

                // Local-state cleared after every Main item, per spec §6.2.2.8.
                hasPendingUsage = false;
                pendingUsage = 0;
                break;

            case 0xA: // Collection
            case 0xC: // End Collection
                // Collections affect the descriptor tree but not the field list
                // we emit. Clear local state per spec.
                hasPendingUsage = false;
                pendingUsage = 0;
                break;

            default:
                // Reserved Main tag — ignore.
                break;
        }
    }

    private static void HandleGlobal(
        int tag, uint data,
        ref ushort usagePage, ref int reportSize, ref int reportCount, ref byte reportId)
    {
        switch (tag)
        {
            case 0x0: // Usage Page
                usagePage = (ushort)data;
                break;
            case 0x7: // Report Size
                reportSize = (int)data;
                break;
            case 0x8: // Report ID
                reportId = (byte)data;
                break;
            case 0x9: // Report Count
                reportCount = (int)data;
                break;
            // Logical/Physical min/max, units, push/pop — irrelevant for
            // (offset, size) emission. Ignored intentionally.
            default:
                break;
        }
    }

    private static void HandleLocal(int tag, uint data, ref uint pendingUsage, ref bool hasPendingUsage)
    {
        if (tag == 0x0)
        {
            pendingUsage = data;
            hasPendingUsage = true;
        }
        // Usage Min/Max and other local items are ignored for now; the probe
        // runner only needs the most recent Usage value.
    }
}

/// <summary>
/// Result of <see cref="HidReportDescriptorParser.Parse(byte[])"/>: the flat
/// field list plus a count of malformed short items skipped during parsing.
/// </summary>
public sealed record HidReportDescriptorParseResult(
    IReadOnlyList<HidReportDescriptorField> Fields,
    int MalformedItemCount);
