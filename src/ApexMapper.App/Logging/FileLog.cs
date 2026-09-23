using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace ApexMapper.App.Logging;

/// <summary>
/// The app's one log file. Any thread may call <see cref="Write"/>: it stamps the line
/// and queues it without touching the disk, and drops it when the queue is full, so a
/// slow or locked disk never holds up the caller. One background thread owns the file.
/// Before a line would take the file past half of the size limit, the file becomes
/// <c>log.1.txt</c>, replacing the previous one, so the two stay within the limit
/// together. While that cannot happen (something holds <c>log.1.txt</c> open) lines
/// keep going to <c>log.txt</c>, up to the whole limit. Lines that could not be written
/// are counted, and a note with the count and the time of the first goes in as soon as
/// one can. Callers log session events and faults, never key presses; the hook, sensor
/// and engine threads never call it.
/// </summary>
public sealed class FileLog : IDisposable
{
    public const int MaxBytes = 1024 * 1024;
    public const int QueueCapacity = 1024;
    public const int DisposeWaitMs = 2000;

    private readonly record struct Entry(string Stamp, string Text)
    {
        public string Line => $"{Stamp} {Text}";
    }

    private readonly BlockingCollection<Entry> _queue = new(QueueCapacity);
    private readonly Lock _dropLock = new();
    private readonly string _path;
    private readonly int _maxBytes;
    private readonly Func<string, Stream> _open;
    private readonly Func<DateTimeOffset> _now;
    private readonly Thread _thread;
    private int _dropped;
    private string? _droppedSince;
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
        _maxBytes = maxBytes;
        _open = open;
        _now = now;
        _thread = new Thread(Run) { IsBackground = true, Name = "apex-log" };
        _thread.Start();
    }

    /// <summary>File operations that failed, for tests.</summary>
    internal int Failures => Volatile.Read(ref _failures);

    /// <summary>Lines lost and not yet noted in the file, for tests.</summary>
    internal int UnnotedDrops => PendingDrops().Count;

    public static string RolledPath(string path) => Path.ChangeExtension(path, ".1.txt");

    /// <summary>Queues one line, stamped now. Never waits on the disk and never throws, including after <see cref="Dispose"/>.</summary>
    public void Write(string message)
    {
        var entry = new Entry(Stamp(), message);
        try
        {
            if (_queue.TryAdd(entry))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // Disposed: the writer has stopped taking lines.
            return;
        }
        Dropped(1, entry.Stamp);
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
        var batch = new List<Entry>();
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            batch.Clear();
            batch.Add(first);
            while (_queue.TryTake(out var more))
            {
                batch.Add(more);
            }
            WriteBatch(batch);
        }
    }

    /// <summary>Log thread. Never throws: the log must not take the app down.</summary>
    private void WriteBatch(List<Entry> entries)
    {
        Stream? stream = null;
        long size = 0;
        var rollFailed = false;
        var next = 0;
        var lost = 0;
        string? firstLost = null;

        // Appends one line, rolling first when it would pass half the limit. False when
        // the line was left out because log.txt is at the whole limit and cannot roll.
        bool Append(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            if (size > 0 && size + bytes.Length > _maxBytes / 2 && !rollFailed)
            {
                stream?.Dispose();
                stream = null;
                try
                {
                    File.Move(_path, RolledPath(_path), overwrite: true);
                    size = 0;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Something holds log.1.txt open: carry on in this file and try again next batch.
                    rollFailed = true;
                    Interlocked.Increment(ref _failures);
                }
            }
            if (size > 0 && size + bytes.Length > _maxBytes)
            {
                return false;
            }
            stream ??= _open(_path);
            stream.Write(bytes);
            size += bytes.Length;
            return true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Read every batch: the user may delete or empty the file while the app runs.
            size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            for (; next < entries.Count; next++)
            {
                if (!Append(entries[next].Line))
                {
                    lost++;
                    firstLost ??= entries[next].Stamp;
                }
            }
            var (dropped, since) = PendingDrops();
            if (dropped > 0 && Append($"{Stamp()} ({dropped} {(dropped == 1 ? "line" : "lines")} dropped from {since} on, while the log was behind)"))
            {
                DropsNoted(dropped);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
            for (; next < entries.Count; next++)
            {
                lost++;
                firstLost ??= entries[next].Stamp;
            }
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
        if (lost > 0)
        {
            Dropped(lost, firstLost!);
        }
    }

    private void Dropped(int count, string since)
    {
        lock (_dropLock)
        {
            _dropped += count;
            _droppedSince ??= since;
        }
    }

    private (int Count, string? Since) PendingDrops()
    {
        lock (_dropLock)
        {
            return (_dropped, _droppedSince);
        }
    }

    /// <summary>The note for these drops is in the file; later drops wait for the next one.</summary>
    private void DropsNoted(int count)
    {
        lock (_dropLock)
        {
            _dropped -= count;
            if (_dropped == 0)
            {
                _droppedSince = null;
            }
        }
    }

    private string Stamp() => _now().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
}
