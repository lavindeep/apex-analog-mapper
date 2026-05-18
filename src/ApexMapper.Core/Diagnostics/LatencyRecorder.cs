using System.Runtime.CompilerServices;
using System.Threading;

namespace ApexMapper.Core.Diagnostics;

/// <summary>
/// Lock-free ring buffer that records latency samples (in microseconds) on the
/// hot path of the binding pipeline. The default <see cref="Null"/> instance is a
/// zero-overhead no-op so production builds incur no cost when diagnostics are
/// disabled.
/// </summary>
public sealed class LatencyRecorder
{
    /// <summary>
    /// Singleton null-object recorder. <see cref="Record"/> is a no-op and
    /// <see cref="TrySnapshot"/> always returns zero.
    /// </summary>
    public static readonly LatencyRecorder Null = new(0);

    private readonly long[] _samples;
    private readonly int _mask;
    private long _writeIndex;

    /// <summary>
    /// Creates a recorder with the given ring buffer capacity. Capacity must be
    /// zero (null-object mode) or a positive power of two.
    /// </summary>
    public LatencyRecorder(int capacity = 4096)
    {
        if (capacity < 0)
        {
            throw new ArgumentException("Capacity must be non-negative.", nameof(capacity));
        }
        if (capacity > 0 && (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));
        }

        _samples = new long[capacity];
        _mask = capacity - 1;
        _writeIndex = 0;
    }

    /// <summary>
    /// Number of samples written since construction (may exceed capacity once
    /// the buffer has wrapped).
    /// </summary>
    public long WriteCount => Interlocked.Read(ref _writeIndex);

    /// <summary>
    /// Records a latency sample in microseconds. Lock-free and allocation-free.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(long microseconds)
    {
        if (_samples.Length == 0)
        {
            return;
        }

        var slot = Interlocked.Increment(ref _writeIndex) - 1;
        _samples[slot & _mask] = microseconds;
    }

    /// <summary>
    /// Copies up to <paramref name="destination"/>.Length most-recent samples
    /// into the destination span in chronological order. Returns the number of
    /// samples copied.
    /// </summary>
    public int TrySnapshot(Span<long> destination)
    {
        if (_samples.Length == 0 || destination.IsEmpty)
        {
            return 0;
        }

        var written = Interlocked.Read(ref _writeIndex);
        if (written <= 0)
        {
            return 0;
        }

        var available = (int)Math.Min(written, (long)_samples.Length);
        var toCopy = Math.Min(available, destination.Length);
        var startWriteIndex = written - toCopy;
        for (var i = 0; i < toCopy; i++)
        {
            var slot = (startWriteIndex + i) & _mask;
            destination[i] = _samples[slot];
        }

        return toCopy;
    }
}
