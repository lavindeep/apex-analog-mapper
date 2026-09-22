using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace ApexMapper.App.Logging;

/// <summary>
/// The app's one log file. Any thread may call <see cref="Write"/>: it stamps the line
/// and queues it without touching the disk, and drops it when the queue is full, so a
/// slow or locked disk never holds up the caller. One background thread owns the file.
/// Before a line would take the file past half of the size limit, the file becomes
/// <c>log.1.txt</c>, replacing the previous one, so the two stay within the limit
/// together. Callers log session events and faults, never key presses; the hook,
/// sensor and engine threads never call it.
/// </summary>
public sealed class FileLog : IDisposable
{
    public const int MaxBytes = 1024 * 1024;
    public const int QueueCapacity = 1024;
    public const int DisposeWaitMs = 2000;

    private readonly BlockingCollection<string> _queue = new(QueueCapacity);
    private readonly string _path;
    private readonly int _rollBytes;
    private readonly Func<string, Stream> _open;
    private readonly Func<DateTimeOffset> _now;
    private readonly Thread _thread;
    private int _dropped;
    private int _failures;
    private int _disposed;

    public FileLog(string path)
        : this(path, MaxBytes, p => new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), () => DateTimeOffset.Now)
    {
    }

    /// <param name="open">Opens the file for appending; tests substitute it to watch or stall the writer.</param>
    internal FileLog(string path, int maxBytes, Func<string, Stream> open, Func<DateTimeOffset> now)
    {
        _path = Path.GetFullPath(path);
        _rollBytes = maxBytes / 2;
        _open = open;
        _now = now;
        _thread = new Thread(Run) { IsBackground = true, Name = "apex-log" };
        _thread.Start();
    }

    /// <summary>Batches the writer could not write, for tests.</summary>
    internal int Failures => Volatile.Read(ref _failures);

    public static string RolledPath(string path) => Path.ChangeExtension(path, ".1.txt");

    /// <summary>Queues one line. Never blocks and never throws, including after <see cref="Dispose"/>.</summary>
    public void Write(string message)
    {
        var line = Stamp(message);
        try
        {
            if (!_queue.TryAdd(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch (InvalidOperationException)
        {
            // Disposed: the writer has stopped taking lines.
        }
    }

    /// <summary>Writes what is queued and stops the writer, waiting at most <see cref="DisposeWaitMs"/>.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _queue.CompleteAdding();
        // A writer stuck on the disk keeps the queue; disposing it under the writer would crash the thread.
        if (_thread.Join(DisposeWaitMs))
        {
            _queue.Dispose();
        }
    }

    private void Run()
    {
        var batch = new List<string>();
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            batch.Clear();
            if (Interlocked.Exchange(ref _dropped, 0) is > 0 and var dropped)
            {
                batch.Add(Stamp($"({dropped} lines dropped while the log was behind)"));
            }
            batch.Add(first);
            while (_queue.TryTake(out var more))
            {
                batch.Add(more);
            }
            WriteBatch(batch);
        }
    }

    /// <summary>Log thread. Any failure drops the batch: the log must never take the app down.</summary>
    private void WriteBatch(List<string> lines)
    {
        Stream? stream = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Read every batch: the user may delete or empty the file while the app runs.
            var size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            foreach (var line in lines)
            {
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                if (size > 0 && size + bytes.Length > _rollBytes)
                {
                    stream?.Dispose();
                    stream = null;
                    File.Move(_path, RolledPath(_path), overwrite: true);
                    size = 0;
                }
                stream ??= _open(_path);
                stream.Write(bytes);
                size += bytes.Length;
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
        }
        try
        {
            // Closing flushes, which can fail too.
            stream?.Dispose();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    private string Stamp(string message) => $"{_now():yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
}
