using System.Globalization;

namespace ApexMapper.Logging;

/// <summary>
/// Pure-function parser for the line format written by <see cref="LogStore"/>:
/// <c>{ISO-8601 UTC} {LEVEL} {message}</c>. Lives in the Logging assembly so
/// the WPF-only <c>ApexMapper.App.Diagnostics.LogTail</c> service can delegate
/// here and unit tests can run cross-platform.
///
/// <para>
/// The parser is intentionally permissive on the message side: any whitespace
/// after the second token is preserved verbatim. Lines that cannot be parsed
/// (empty, mid-line truncations, malformed timestamps) are reported via the
/// <c>malformedCount</c> out-parameter on <see cref="ParseLines"/> rather than
/// throwing — log files written concurrently with reading will occasionally
/// expose mid-line tails, and surfacing them as exceptions would crash the
/// diagnostics view.
/// </para>
/// </summary>
public static class LogParser
{
    /// <summary>
    /// Attempts to parse a single line. Returns <c>true</c> on success with
    /// <paramref name="entry"/> populated; otherwise returns <c>false</c> and
    /// sets <paramref name="entry"/> to <c>null</c>.
    ///
    /// <para>
    /// A line is well-formed when it contains at least an ISO-8601 timestamp
    /// and a level token (whitespace-separated). The message is everything
    /// after the level token, including embedded whitespace; an empty message
    /// is permitted.
    /// </para>
    /// </summary>
    public static bool TryParseLine(string line, out LogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        // Find the first whitespace — separates timestamp from level.
        var firstSpace = IndexOfWhitespace(line, 0);
        if (firstSpace < 0)
        {
            return false;
        }

        var tsToken = line.AsSpan(0, firstSpace);
        if (!DateTime.TryParseExact(
                tsToken,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return false;
        }
        // ParseExact with the round-trip "O" specifier preserves Kind. Force
        // UTC for any inputs that round-trip as Local or Unspecified (the
        // "Z" suffix already makes this true for LogStore output, but guard
        // against historical / hand-edited inputs).
        if (timestamp.Kind != DateTimeKind.Utc)
        {
            timestamp = DateTime.SpecifyKind(timestamp.ToUniversalTime(), DateTimeKind.Utc);
        }

        var levelStart = firstSpace + 1;
        if (levelStart >= line.Length)
        {
            return false;
        }
        var secondSpace = IndexOfWhitespace(line, levelStart);
        string level;
        string message;
        if (secondSpace < 0)
        {
            // Only two tokens: timestamp + level. Empty message is OK.
            level = line.Substring(levelStart);
            message = string.Empty;
        }
        else
        {
            level = line.Substring(levelStart, secondSpace - levelStart);
            // Preserve message verbatim from the byte after the level's
            // single delimiting space — keeps any leading whitespace the
            // user wrote into the message body.
            message = line.Substring(secondSpace + 1);
        }

        if (level.Length == 0)
        {
            return false;
        }

        entry = new LogEntry(timestamp, level, message);
        return true;
    }

    /// <summary>
    /// Parses every line in <paramref name="lines"/>, returning successfully
    /// parsed entries and reporting the count of malformed lines via
    /// <paramref name="malformedCount"/>. Order is preserved.
    /// </summary>
    public static IReadOnlyList<LogEntry> ParseLines(IEnumerable<string> lines, out int malformedCount)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var entries = new List<LogEntry>();
        var malformed = 0;
        foreach (var line in lines)
        {
            if (TryParseLine(line, out var entry) && entry is not null)
            {
                entries.Add(entry);
            }
            else
            {
                malformed++;
            }
        }
        malformedCount = malformed;
        return entries;
    }

    /// <summary>
    /// Returns the subset of <paramref name="entries"/> whose level matches
    /// any token in <paramref name="levels"/>. Matching is case-insensitive
    /// using ordinal comparison.
    ///
    /// <para>
    /// An empty <paramref name="levels"/> collection returns an empty list —
    /// the diagnostics UI maps "no level toggles enabled" to "show nothing".
    /// </para>
    /// </summary>
    public static IReadOnlyList<LogEntry> FilterByLevels(
        IReadOnlyList<LogEntry> entries,
        IReadOnlyCollection<string> levels)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0)
        {
            return Array.Empty<LogEntry>();
        }

        // Build a small case-insensitive set so the inner loop is O(1).
        var allowed = new HashSet<string>(levels, StringComparer.OrdinalIgnoreCase);
        var result = new List<LogEntry>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (allowed.Contains(entries[i].Level))
            {
                result.Add(entries[i]);
            }
        }
        return result;
    }

    private static int IndexOfWhitespace(string s, int startIndex)
    {
        for (var i = startIndex; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                return i;
            }
        }
        return -1;
    }
}
