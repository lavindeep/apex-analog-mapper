namespace ApexMapper.App.Diagnostics;

/// <summary>
/// Loads recent log entries from the rolling log files and supports level
/// filtering.
/// </summary>
public interface ILogTail
{
    /// <summary>Loads up to <paramref name="maxLines"/> most-recent entries.</summary>
    IReadOnlyList<LogTailEntry> Load(int maxLines);

    /// <summary>Filters the supplied entries by the requested set of levels.</summary>
    IReadOnlyList<LogTailEntry> Filter(IReadOnlyList<LogTailEntry> entries, IReadOnlyCollection<string> levels);
}
