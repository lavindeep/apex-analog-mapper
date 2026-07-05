using System.Text;

namespace ApexMapper.Persistence.Atomic;

internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Default age past which an orphaned temp file is considered abandoned and swept.</summary>
    private static readonly TimeSpan DefaultTempTtl = TimeSpan.FromMinutes(1);

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var tmp = WriteTemp(path, bytes);
        try
        {
            Commit(tmp, path);
        }
        catch
        {
            DiscardTemp(tmp);
            throw;
        }
    }

    public static void WriteAllText(string path, string contents)
        => WriteAllBytes(path, Utf8NoBom.GetBytes(contents));

    /// <summary>Writes <paramref name="bytes"/> to a fresh temp file next to <paramref name="path"/>, fsyncs it, and returns the temp path. Does not touch the target.</summary>
    public static string WriteTemp(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            DiscardTemp(tmp);
            throw;
        }
        return tmp;
    }

    public static string WriteTemp(string path, string contents)
        => WriteTemp(path, Utf8NoBom.GetBytes(contents));

    /// <summary>Atomically moves a staged temp file onto <paramref name="targetPath"/>.</summary>
    public static void Commit(string tempPath, string targetPath) => Commit(tempPath, targetPath, File.Replace);

    internal static void Commit(string tempPath, string targetPath, Action<string, string, string?> replace)
    {
        if (File.Exists(targetPath))
        {
            try
            {
                replace(tempPath, targetPath, null);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
            {
                // File.Replace is unsupported on some filesystems (e.g. FAT/exFAT, certain network
                // shares). Fall back to delete+move. This opens a brief non-atomic window where a
                // crash could leave the target missing; acceptable because only rare non-atomic-
                // rename volumes reach here, and the fresh content is already durably on disk.
                File.Delete(targetPath);
                File.Move(tempPath, targetPath);
            }
        }
        else
        {
            File.Move(tempPath, targetPath);
        }
    }

    /// <summary>Best-effort deletion of a temp file. Never throws.</summary>
    public static void DiscardTemp(string tempPath)
    {
        try { File.Delete(tempPath); } catch { /* best effort */ }
    }

    /// <summary>
    /// Best-effort sweep of orphaned <c>*.tmp.*</c> files left behind by crashed writes.
    /// Only removes temps older than <paramref name="olderThan"/> (default one minute) so an
    /// in-flight write is never disturbed. Never throws.
    /// </summary>
    public static void SweepStaleTemps(string directory, TimeSpan? olderThan = null)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var cutoff = DateTime.UtcNow - (olderThan ?? DefaultTempTtl);
            foreach (var file in Directory.EnumerateFiles(directory, "*.tmp.*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* skip files we cannot stat or delete */ }
            }
        }
        catch { /* best effort */ }
    }
}
