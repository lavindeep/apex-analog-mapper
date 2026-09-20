namespace ApexMapper.Core.Storage;

public enum LoadStatus
{
    /// <summary>The primary file parsed.</summary>
    Loaded,

    /// <summary>No file exists; use defaults.</summary>
    NotFound,

    /// <summary>The primary file was unreadable; the backup parsed and was restored.</summary>
    Recovered,

    /// <summary>Neither the primary nor the backup parsed; the primary was set aside as .corrupt.</summary>
    Corrupt,
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
        if (File.Exists(path))
        {
            File.Replace(temp, path, BackupPath(path), ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// Loads the primary file, or recovers from the backup. <paramref name="parse"/>
    /// returns null and an error for unreadable text.
    /// </summary>
    public static LoadResult<T> Load<T>(string path, Func<string, (T? Value, string? Error)> parse) where T : class
    {
        if (!File.Exists(path))
        {
            return new LoadResult<T>(null, LoadStatus.NotFound, null);
        }
        var (value, error) = TryParse(path, parse);
        if (value is not null)
        {
            return new LoadResult<T>(value, LoadStatus.Loaded, null);
        }

        // Keep the unreadable file for the user; never delete it.
        try
        {
            File.Move(path, CorruptPath(path), overwrite: true);
        }
        catch (IOException)
        {
        }

        var backup = BackupPath(path);
        if (File.Exists(backup))
        {
            var (recovered, backupError) = TryParse(backup, parse);
            if (recovered is not null)
            {
                Save(path, File.ReadAllText(backup));
                return new LoadResult<T>(recovered, LoadStatus.Recovered, error);
            }
            error = $"{error} The backup is unreadable too: {backupError}";
        }
        return new LoadResult<T>(null, LoadStatus.Corrupt, error);
    }

    private static (T? Value, string? Error) TryParse<T>(string path, Func<string, (T? Value, string? Error)> parse) where T : class
    {
        try
        {
            return parse(File.ReadAllText(path));
        }
        catch (IOException e)
        {
            return (null, e.Message);
        }
    }
}
