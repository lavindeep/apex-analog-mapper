using System.Globalization;
using System.Text;

namespace ApexMapper.Logging;

public sealed class LogStore : IDisposable
{
    private readonly string _dir;
    private readonly string _baseFile;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly Action<string, string> _move;
    private readonly object _lock = new();
    private FileStream? _stream;

    /// <summary>
    /// Number of rotations skipped because the active file could not be moved aside
    /// (e.g. a reader on Windows holds it without <see cref="FileShare.Delete"/>). While
    /// skips accumulate the active file grows past the byte cap until the reader releases it.
    /// </summary>
    public int RotationSkips { get; private set; }

    /// <param name="maxFiles">
    /// Total number of log files to retain, <em>including</em> the active file — e.g. 5 keeps
    /// <c>app.log</c> plus <c>app.log.1</c> … <c>app.log.4</c>. Must be &gt;= 1.
    /// </param>
    public LogStore(string directory, string baseFileName, long maxBytes, int maxFiles)
        : this(directory, baseFileName, maxBytes, maxFiles, File.Move)
    {
    }

    internal LogStore(string directory, string baseFileName, long maxBytes, int maxFiles, Action<string, string> move)
    {
        if (maxBytes <= 0) throw new ArgumentException("maxBytes must be > 0", nameof(maxBytes));
        if (maxFiles < 1) throw new ArgumentException("maxFiles must be >= 1", nameof(maxFiles));
        _dir = directory;
        _baseFile = baseFileName;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        _move = move;
        Directory.CreateDirectory(_dir);
    }

    private string ActivePath => Path.Combine(_dir, _baseFile);

    public void Write(LogLevel level, string message)
    {
        lock (_lock)
        {
            EnsureStream();
            var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O} {level.ToString().ToUpperInvariant()} {message}\n");
            var bytes = Encoding.UTF8.GetBytes(line);
            if (_stream!.Position + bytes.Length > _maxBytes && _stream.Position > 0)
            {
                Rotate();
                EnsureStream();
            }
            _stream!.Write(bytes, 0, bytes.Length);
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            _stream?.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void EnsureStream()
    {
        if (_stream != null) return;
        _stream = new FileStream(ActivePath, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private void Rotate()
    {
        _stream?.Dispose();
        _stream = null;

        // maxFiles counts the active file too, so there are (maxFiles - 1) archive slots:
        // app.log.1 … app.log.(maxFiles-1).
        var archives = _maxFiles - 1;
        var staged = Path.Combine(_dir, $"{_baseFile}.rotating");

        // The entire rotation is one guarded unit. A reader holding any of these files
        // without FileShare.Delete (e.g. the diagnostics log tail on Windows) makes a move
        // or delete throw IOException; when that happens anywhere in the sequence, count a
        // skip and let Write keep appending to the active file instead of throwing. On
        // POSIX renames succeed even under a reader, so the guard is a no-op there.
        // A partial rotation is safe: shifts run highest-slot-first and a failure aborts
        // the remaining moves, so an older generation never overwrites a newer one — at
        // worst the chain is left with a hole that later rotations shift past.
        try
        {
            if (archives <= 0)
            {
                // No archive slots configured: discard the active file's contents.
                if (File.Exists(staged)) File.Delete(staged);
                if (File.Exists(ActivePath)) File.Delete(ActivePath);
                return;
            }

            if (File.Exists(staged)) File.Delete(staged);
            if (File.Exists(ActivePath))
            {
                _move(ActivePath, staged);
                ArchiveStaged(staged, archives);
            }
        }
        catch (IOException)
        {
            RotationSkips++;
        }
    }

    /// <summary>Shifts the archive chain up one slot and moves the staged file into <c>.1</c>.</summary>
    private void ArchiveStaged(string staged, int archives)
    {
        var oldest = Path.Combine(_dir, $"{_baseFile}.{archives}");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = archives - 1; i >= 1; i--)
        {
            var src = Path.Combine(_dir, $"{_baseFile}.{i}");
            var dst = Path.Combine(_dir, $"{_baseFile}.{i + 1}");
            if (!File.Exists(src)) continue;
            // A slot left occupied by an earlier partial rotation holds older content than
            // the generation moving in; drop it so the chain self-heals instead of wedging.
            if (File.Exists(dst)) File.Delete(dst);
            _move(src, dst);
        }
        _move(staged, Path.Combine(_dir, $"{_baseFile}.1"));
    }
}
