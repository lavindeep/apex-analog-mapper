using System.Text;
using ApexMapper.Logging;

namespace ApexMapper.App.Diagnostics.LogTail;

/// <summary>
/// Loads the most-recent log lines from a <see cref="LogStore"/>-managed
/// directory. Reads the active file first; if it contains fewer than
/// <c>maxLines</c> records, walks rotated files (<c>{base}.1</c>,
/// <c>{base}.2</c>, ...) until enough lines are accumulated.
///
/// <para>
/// Parsing is delegated to <see cref="LogParser"/> so the IO-free logic is
/// exercised cross-platform. <c>LogTail</c> itself converts the resulting
/// <see cref="LogEntry"/> records into the App-owned
/// <see cref="LogTailEntry"/> shape that the WPF view binds to.
/// </para>
///
/// <para>
/// Files are opened with <c>FileShare.ReadWrite</c> so reads succeed even
/// while the <see cref="LogStore"/> writer holds the active file open for
/// append. Missing files (e.g. the log has never been written) are treated
/// as empty rather than throwing.
/// </para>
/// </summary>
public sealed class LogTail : ILogTail
{
    private readonly Func<string> _getLogFilePath;

    /// <summary>
    /// Constructs a tail bound to the active log path produced by
    /// <paramref name="getLogFilePath"/>. The function is invoked on every
    /// <see cref="Load"/> so tests can hand in a temp path without lifecycle
    /// concerns and production callers can defer the path until the logging
    /// directory has been initialised.
    /// </summary>
    public LogTail(Func<string> getLogFilePath)
    {
        ArgumentNullException.ThrowIfNull(getLogFilePath);
        _getLogFilePath = getLogFilePath;
    }

    /// <summary>
    /// Count of lines parsed unsuccessfully on the most recent
    /// <see cref="Load"/> call. Zero before the first load.
    /// </summary>
    public int MalformedCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<LogTailEntry> Load(int maxLines)
    {
        if (maxLines <= 0)
        {
            MalformedCount = 0;
            return Array.Empty<LogTailEntry>();
        }

        var activePath = _getLogFilePath();

        // Collect lines newest-last (chronological), walking rotated files
        // when the active file is short. Stop the walk as soon as we have at
        // least `maxLines` lines accumulated; we'll trim to exactly `maxLines`
        // before parsing.
        var collected = new List<string>(maxLines);
        var fileIndex = 0;
        while (collected.Count < maxLines)
        {
            var path = fileIndex == 0
                ? activePath
                : activePath + "." + fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!File.Exists(path))
            {
                break;
            }

            var lines = SafeReadAllLines(path);
            // Older entries belong before the previously-collected newer
            // ones, so prepend.
            collected.InsertRange(0, lines);
            fileIndex++;
        }

        // Take the last `maxLines` entries (most recent).
        IReadOnlyList<string> trimmed = collected.Count <= maxLines
            ? collected
            : collected.GetRange(collected.Count - maxLines, maxLines);

        var entries = LogParser.ParseLines(trimmed, out var malformed);
        MalformedCount = malformed;

        var mapped = new List<LogTailEntry>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            mapped.Add(new LogTailEntry(e.TimestampUtc, e.Level, e.Message));
        }
        return mapped;
    }

    /// <inheritdoc />
    public IReadOnlyList<LogTailEntry> Filter(IReadOnlyList<LogTailEntry> entries, IReadOnlyCollection<string> levels)
        => LogTailFilter.Apply(entries, levels);

    private static IReadOnlyList<string> SafeReadAllLines(string path)
    {
        // FileShare.ReadWrite so the live LogStore writer (which keeps the
        // active file open for append) doesn't lock us out. Open with
        // explicit UTF-8 decoding — LogStore writes UTF-8 without BOM.
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
            return lines;
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }
}
