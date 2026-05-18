using ApexMapper.Logging;

namespace ApexMapper.App.Diagnostics.LogTail;

/// <summary>
/// Pure-function level filter for <see cref="LogTailEntry"/>. Delegates to
/// <see cref="LogParser.FilterByLevels"/> after re-projecting through the
/// cross-platform <see cref="LogEntry"/> shape; matching is case-insensitive
/// and an empty level set returns an empty list (mirrors UI semantics where
/// "no level toggles enabled" hides everything).
/// </summary>
public static class LogTailFilter
{
    /// <summary>Returns the subset of <paramref name="entries"/> whose level is in <paramref name="levels"/>.</summary>
    public static IReadOnlyList<LogTailEntry> Apply(
        IReadOnlyList<LogTailEntry> entries,
        IReadOnlyCollection<string> levels)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0)
        {
            return Array.Empty<LogTailEntry>();
        }

        var allowed = new HashSet<string>(levels, StringComparer.OrdinalIgnoreCase);
        var result = new List<LogTailEntry>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (allowed.Contains(entries[i].Level))
            {
                result.Add(entries[i]);
            }
        }
        return result;
    }
}
