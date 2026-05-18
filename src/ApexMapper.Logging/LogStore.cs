using System.Globalization;
using System.Text;

namespace ApexMapper.Logging;

public sealed class LogStore : IDisposable
{
    private readonly string _dir;
    private readonly string _baseFile;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly object _lock = new();
    private FileStream? _stream;

    public LogStore(string directory, string baseFileName, long maxBytes, int maxFiles)
    {
        if (maxBytes <= 0) throw new ArgumentException("maxBytes must be > 0", nameof(maxBytes));
        if (maxFiles < 1) throw new ArgumentException("maxFiles must be >= 1", nameof(maxFiles));
        _dir = directory;
        _baseFile = baseFileName;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
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

        for (var i = _maxFiles - 1; i >= 1; i--)
        {
            var src = Path.Combine(_dir, $"{_baseFile}.{i}");
            var dst = Path.Combine(_dir, $"{_baseFile}.{i + 1}");
            if (File.Exists(dst)) File.Delete(dst);
            if (File.Exists(src)) File.Move(src, dst);
        }
        var oneBack = Path.Combine(_dir, $"{_baseFile}.1");
        if (File.Exists(oneBack)) File.Delete(oneBack);
        if (File.Exists(ActivePath)) File.Move(ActivePath, oneBack);

        var overflow = Path.Combine(_dir, $"{_baseFile}.{_maxFiles}");
        if (File.Exists(overflow)) File.Delete(overflow);
    }
}
