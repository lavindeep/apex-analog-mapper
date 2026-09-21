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

    /// <summary>The primary file could not be read (locked or access denied). Nothing was moved or written.</summary>
    Unavailable,
}

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
            // Replace can refuse (volume quirks, an odd backup file). Two steps still
            // never leave a partial primary; only the backup can lag by a crash.
            File.Copy(path, BackupPath(path), overwrite: true);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>
    /// Loads the primary file, or recovers from the backup. <paramref name="parse"/>
    /// returns null and an error for unreadable text; anything it throws counts as
    /// unreadable text too, since the file is untrusted input.
    /// </summary>
    public static LoadResult<T> Load<T>(string path, Func<string, (T? Value, string? Error)> parse) where T : class
    {
        var backup = BackupPath(path);
        if (!File.Exists(path))
        {
            if (File.Exists(backup) && TryRead(backup, parse).Value is { } fromBackup)
            {
                var restoreError = TryRestore(path, backup);
                return new LoadResult<T>(fromBackup, LoadStatus.Recovered, restoreError);
            }
            return new LoadResult<T>(null, LoadStatus.NotFound, null);
        }

        var (value, error, unreadable) = TryRead(path, parse);
        if (value is not null)
        {
            return new LoadResult<T>(value, LoadStatus.Loaded, null);
        }
        if (unreadable)
        {
            return new LoadResult<T>(null, LoadStatus.Unavailable, error);
        }

        // Keep the unreadable file for the user; never delete it.
        try
        {
            File.Move(path, CorruptPath(path), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        if (File.Exists(backup))
        {
            var (recovered, backupError, _) = TryRead(backup, parse);
            if (recovered is not null)
            {
                var restoreError = TryRestore(path, backup);
                return new LoadResult<T>(recovered, LoadStatus.Recovered, restoreError is null ? error : $"{error} {restoreError}");
            }
            error = $"{error} The backup is unreadable too: {backupError}";
        }
        return new LoadResult<T>(null, LoadStatus.Corrupt, error);
    }

    private static string? TryRestore(string path, string backup)
    {
        try
        {
            Save(path, File.ReadAllText(backup));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The file could not be rewritten from its backup: {e.Message}";
        }
    }

    private static (T? Value, string? Error, bool Unreadable) TryRead<T>(string path, Func<string, (T? Value, string? Error)> parse) where T : class
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, e.Message, true);
        }
        try
        {
            var (value, error) = parse(text);
            return (value, error, false);
        }
        catch (Exception e)
        {
            return (null, "The file could not be read: " + e.Message, false);
        }
    }
}
