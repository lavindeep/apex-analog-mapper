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

    /// <summary>
    /// A newer version of the app wrote the file. Nothing was moved, restored or written,
    /// and callers must not save over it: going back a version must never cost the user
    /// the newer version's data.
    /// </summary>
    Newer,
}

/// <param name="Error">
/// For <see cref="LoadStatus.Recovered"/>, why the primary could not be rewritten from
/// the backup, or null when it was. Otherwise why the file could not be used, including
/// what happened to an unreadable one.
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
            // Replace can refuse (volume quirks, an odd backup file). Two steps still
            // never leave a partial primary; only the backup can lag by a crash.
            File.Copy(path, BackupPath(path), overwrite: true);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>
    /// Loads the primary file, or recovers from the backup. Anything
    /// <paramref name="parse"/> throws counts as unreadable text, since the file is
    /// untrusted input. A file the parser marks newer is left exactly as it is.
    /// </summary>
    public static LoadResult<T> Load<T>(string path, Func<string, Parsed<T>> parse) where T : class
    {
        var backup = BackupPath(path);
        if (!File.Exists(path))
        {
            var lone = File.Exists(backup) ? TryRead(backup, parse).Parsed : default;
            if (lone.Newer)
            {
                return new LoadResult<T>(null, LoadStatus.Newer, lone.Error);
            }
            return lone.Value is { } fromBackup
                ? new LoadResult<T>(fromBackup, LoadStatus.Recovered, TryRestore(path, backup))
                : new LoadResult<T>(null, LoadStatus.NotFound, null);
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

        // Keep the unreadable file for the user; never delete it.
        var error = $"{primary.Error} {SetAside(path)}";
        if (File.Exists(backup))
        {
            var (recovered, _) = TryRead(backup, parse);
            if (recovered.Newer)
            {
                return new LoadResult<T>(null, LoadStatus.Newer, recovered.Error);
            }
            if (recovered.Value is { } fromBackup)
            {
                return new LoadResult<T>(fromBackup, LoadStatus.Recovered, TryRestore(path, backup));
            }
            error = $"{error} The backup is unreadable too: {recovered.Error}";
        }
        return new LoadResult<T>(null, LoadStatus.Corrupt, error);
    }

    private static string SetAside(string path)
    {
        try
        {
            File.Move(path, CorruptPath(path), overwrite: true);
            return $"It was kept as {Path.GetFileName(CorruptPath(path))}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"It could not be moved aside: {e.Message}";
        }
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
            return e.Message;
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
