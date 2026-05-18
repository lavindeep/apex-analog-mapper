namespace ApexMapper.App.Diagnostics.LogTail;

/// <summary>
/// Pure-function level filter for <see cref="LogTailEntry"/>. Mirrors the
/// semantics of <see cref="ApexMapper.Logging.LogParser.FilterByLevels"/>
/// for the App-side entry shape: matching is case-insensitive ordinal, and an
/// empty level set returns an empty list (UI rule: "no level toggles enabled"
/// hides everything).
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
