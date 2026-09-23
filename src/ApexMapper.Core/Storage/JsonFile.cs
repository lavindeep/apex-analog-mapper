namespace ApexMapper.Core.Storage;

public enum LoadStatus
{
    /// <summary>The primary file parsed.</summary>
    Loaded,

    /// <summary>No file and no backup exist; use defaults.</summary>
    NotFound,

    /// <summary>The primary file was unreadable or missing; the backup parsed and was restored.</summary>
    Recovered,

    /// <summary>Neither the primary nor the backup parsed; the primary was set aside as .corrupt.</summary>
    Corrupt,

    /// <summary>The primary file, or the backup it would be recovered from, could not be read (locked or access denied). Nothing was moved or written.</summary>
    Unavailable,

    /// <summary>
    /// A newer version of the app wrote the file. Nothing was moved, restored or written,
    /// and callers must not save over it: going back a version must never cost the user
    /// the newer version's data.
    /// </summary>
    Newer,
}

/// <param name="Error">
/// For <see cref="LoadStatus.Recovered"/>, what happened to a damaged primary and whether
/// it could be rewritten from the backup, or null when a missing primary was restored.
/// Otherwise why the file could not be used, including what happened to an unreadable one.
/// </param>
public sealed record LoadResult<T>(T? Value, LoadStatus Status, string? Error);

/// <summary>
/// Small JSON files written atomically with one backup. Save writes a temp file in
/// the same directory, flushes it to disk, then swaps it in with <c>File.Replace</c>,
/// which moves the previous file to <c>.bak</c> in the same step. A crash at any point
/// leaves either the old file or the new one, never a partial one.
/// </summary>
public static class JsonFile
{
    public static string BackupPath(string path) => path + ".bak";

    public static string CorruptPath(string path) => path + ".corrupt";

    private static string TempPath(string path) => path + ".tmp";

    public static void Save(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = TempPath(path);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        if (!File.Exists(path))
        {
            File.Move(temp, path);
            return;
        }
        try
        {
            File.Replace(temp, path, BackupPath(path), ignoreMetadataErrors: true);
        }
        catch (IOException)
        {
            // Replace can refuse (volume quirks, an odd backup file, a primary held open).
            // The new file goes in before the old one becomes the backup, so a failure
            // leaves the backup as it was. None of the steps leaves a partial primary; a
            // crash between them leaves the old primary in the side file.
            var previous = PreviousPath(path);
            File.Copy(path, previous, overwrite: true);
            File.Move(temp, path, overwrite: true);
            File.Move(previous, BackupPath(path), overwrite: true);
        }
    }

    private static string PreviousPath(string path) => path + ".old";

    /// <summary>
    /// Loads the primary file, or recovers from the backup. Anything
    /// <paramref name="parse"/> throws counts as unreadable text, since the file is
    /// untrusted input. A file the parser marks newer is left exactly as it is.
    /// </summary>
    public static LoadResult<T> Load<T>(string path, Func<string, Parsed<T>> parse) where T : class
    {
        var backup = BackupPath(path);
        var hasBackup = File.Exists(backup);
        if (!File.Exists(path))
        {
            if (!hasBackup)
            {
                return new LoadResult<T>(null, LoadStatus.NotFound, null);
            }
            var (lone, loneUnreadable) = TryRead(backup, parse);
            return lone switch
            {
                _ when loneUnreadable => new LoadResult<T>(null, LoadStatus.Unavailable, lone.Error),
                { Newer: true } => new LoadResult<T>(null, LoadStatus.Newer, lone.Error),
                { Value: { } restored } => new LoadResult<T>(restored, LoadStatus.Recovered, Restore(path, backup, null)),
                _ => new LoadResult<T>(null, LoadStatus.NotFound, null),
            };
        }

        var (primary, unreadable) = TryRead(path, parse);
        if (primary.Value is { } value)
        {
            return new LoadResult<T>(value, LoadStatus.Loaded, null);
        }
        if (unreadable)
        {
            return new LoadResult<T>(null, LoadStatus.Unavailable, primary.Error);
        }
        if (primary.Newer)
        {
            return new LoadResult<T>(null, LoadStatus.Newer, primary.Error);
        }

        // The primary holds text no version can read. Look at the backup before moving
        // anything: a backup that cannot be opened, or a newer one, leaves both alone.
        var (secondary, backupUnreadable) = hasBackup ? TryRead(backup, parse) : default;
        if (backupUnreadable)
        {
            return new LoadResult<T>(null, LoadStatus.Unavailable, secondary.Error);
        }
        if (secondary.Newer)
        {
            return new LoadResult<T>(null, LoadStatus.Newer, secondary.Error);
        }

        // Keep the unreadable file for the user; never delete it.
        var aside = SetAside(path);
        if (secondary.Value is { } fromBackup)
        {
            return new LoadResult<T>(fromBackup, LoadStatus.Recovered, Restore(path, backup, aside));
        }
        var error = $"{primary.Error} {aside}";
        return new LoadResult<T>(null, LoadStatus.Corrupt, hasBackup ? $"{error} The backup is unreadable too: {secondary.Error}" : error);
    }

    private static string SetAside(string path)
    {
        try
        {
            File.Move(path, CorruptPath(path), overwrite: true);
            return $"The damaged file was kept as {Path.GetFileName(CorruptPath(path))}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The damaged file could not be moved aside: {e.Message}";
        }
    }

    /// <summary>Rewrites the primary from the backup. Returns what the user should know, or null when there is nothing to add.</summary>
    private static string? Restore(string path, string backup, string? aside)
    {
        try
        {
            Save(path, File.ReadAllText(backup));
            return aside;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{aside} The file could not be rewritten from its backup: {e.Message}".TrimStart();
        }
    }

    private static (Parsed<T> Parsed, bool Unreadable) TryRead<T>(string path, Func<string, Parsed<T>> parse) where T : class
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new Parsed<T>(null, e.Message), true);
        }
        try
        {
            return (parse(text), false);
        }
        catch (Exception e)
        {
            return (new Parsed<T>(null, "The file could not be read: " + e.Message), false);
        }
    }
}
