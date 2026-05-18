using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ApexMapper.Core.Diagnostics;

/// <summary>
/// In-house log-bucketed latency histogram (microseconds). Records are placed
/// into one of 24 power-of-two "octaves" subdivided into 16 linear sub-buckets,
/// for 384 total buckets covering [1, 16,777,216) microseconds (≈16.8 seconds).
///
/// <para>
/// The two-level layout — exponent (octave) plus 4-bit mantissa — gives a fixed
/// relative resolution of 1/16 ≈ 6.25 % per sub-bucket, and combined with linear
/// interpolation across the cumulative distribution yields well under 1 % error
/// on smooth distributions while remaining lock-free and allocation-free.
/// </para>
///
/// <para>
/// Threading: <see cref="Record"/> uses <see cref="Interlocked.Increment"/> on a
/// long[] and is safe to call from any thread. <see cref="Percentiles"/> and
/// <see cref="Reset"/> are intended for the single sampling/draining thread and
/// take a best-effort snapshot under concurrent recording (counts may shift
/// slightly between buckets, but never lose samples).
/// </para>
/// </summary>
public sealed class HdrHistogram
{
    /// <summary>Number of power-of-two octaves covered (2^0 .. 2^24 µs).</summary>
    public const int OctaveCount = 24;

    /// <summary>Linear sub-buckets per octave (4-bit mantissa).</summary>
    public const int SubBucketCount = 16;

    /// <summary>Total bucket count = <see cref="OctaveCount"/> × <see cref="SubBucketCount"/>.</summary>
    public const int TotalBuckets = OctaveCount * SubBucketCount;

    private readonly long[] _buckets;
    private long _total;

    /// <summary>Creates an empty histogram with all buckets zeroed.</summary>
    public HdrHistogram()
    {
        _buckets = new long[TotalBuckets];
    }

    /// <summary>Number of samples recorded since the last <see cref="Reset"/>.</summary>
    public long TotalCount => Interlocked.Read(ref _total);

    /// <summary>Read-only view of the raw bucket counts (length <see cref="TotalBuckets"/>).</summary>
    public IReadOnlyList<long> Buckets => _buckets;

    /// <summary>
    /// Records a sample in microseconds. Negative and zero values are clamped to
    /// the lowest bucket; values above the histogram's range are clamped to the
    /// highest bucket. Lock-free and allocation-free.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(long microseconds)
    {
        var index = BucketIndex(microseconds);
        Interlocked.Increment(ref _buckets[index]);
        Interlocked.Increment(ref _total);
    }

    /// <summary>Zeros all buckets and the running total.</summary>
    public void Reset()
    {
        Array.Clear(_buckets, 0, _buckets.Length);
        Interlocked.Exchange(ref _total, 0);
    }

    /// <summary>
    /// Returns P50/P95/P99 in microseconds in a single pass over the bucket
    /// array. Uses linear interpolation across the cumulative distribution to
    /// keep error well below the 6.25 % bucket width.
    /// </summary>
    public (long P50, long P95, long P99) Percentiles()
    {
        var total = Interlocked.Read(ref _total);
        if (total == 0)
        {
            return (0, 0, 0);
        }

        // Targets in fractional sample-position space.
        var t50 = total * 0.50;
        var t95 = total * 0.95;
        var t99 = total * 0.99;

        long p50 = 0, p95 = 0, p99 = 0;
        var found50 = false;
        var found95 = false;
        var found99 = false;

        double cumulative = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            var count = _buckets[i];
            if (count == 0)
            {
                continue;
            }

            var nextCumulative = cumulative + count;

            if (!found50 && t50 <= nextCumulative)
            {
                p50 = InterpolateBucket(i, t50, cumulative, count);
                found50 = true;
            }
            if (!found95 && t95 <= nextCumulative)
            {
                p95 = InterpolateBucket(i, t95, cumulative, count);
                found95 = true;
            }
            if (!found99 && t99 <= nextCumulative)
            {
                p99 = InterpolateBucket(i, t99, cumulative, count);
                found99 = true;
                break;
            }

            cumulative = nextCumulative;
        }

        // If we ran out of samples (e.g. total=1 and target falls past), fall
        // back to the last non-empty bucket's upper bound.
        if (!found50 || !found95 || !found99)
        {
            long lastUpper = 0;
            for (var i = _buckets.Length - 1; i >= 0; i--)
            {
                if (_buckets[i] != 0)
                {
                    lastUpper = BucketUpperBound(i);
                    break;
                }
            }
            if (!found50) p50 = lastUpper;
            if (!found95) p95 = lastUpper;
            if (!found99) p99 = lastUpper;
        }

        return (p50, p95, p99);
    }

    /// <summary>
    /// Returns the bucket index for a sample. Octave = floor(log2(clamped)),
    /// sub-bucket = top 4 bits of the mantissa within the octave.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketIndex(long microseconds)
    {
        if (microseconds < 1)
        {
            return 0;
        }
        if (microseconds >= (1L << OctaveCount))
        {
            return TotalBuckets - 1;
        }

        // Octave = position of highest set bit.
        var octave = BitOperations.Log2((ulong)microseconds);

        // Within the octave [2^octave, 2^(octave+1)), split into 16 linear
        // sub-buckets by taking the top 4 bits below the leading 1.
        // sub = (microseconds - 2^octave) >> (octave - 4) when octave >= 4,
        // else sub = (microseconds - 2^octave) << (4 - octave).
        long mantissa = microseconds - (1L << octave);
        int sub;
        if (octave >= 4)
        {
            sub = (int)(mantissa >> (octave - 4));
        }
        else
        {
            sub = (int)(mantissa << (4 - octave));
        }
        if (sub > SubBucketCount - 1) sub = SubBucketCount - 1;
        if (sub < 0) sub = 0;

        return octave * SubBucketCount + sub;
    }

    /// <summary>
    /// Lower-bound microsecond value covered by the given bucket index.
    /// </summary>
    private static long BucketLowerBound(int index)
    {
        if (index <= 0) return 1;
        var octave = index / SubBucketCount;
        var sub = index % SubBucketCount;
        long octaveBase = 1L << octave;
        long subWidth = octave >= 4 ? (octaveBase >> 4) : 1;
        // For octaves < 4 the sub-buckets are 1 µs wide and indexes < 16 cover
        // the whole octave (the upper part collapses to the same lower bound).
        long lower = octaveBase + sub * subWidth;
        return Math.Max(1, lower);
    }

    /// <summary>
    /// Exclusive upper-bound microsecond value covered by the given bucket index.
    /// For the topmost bucket we clamp to (1 &lt;&lt; OctaveCount).
    /// </summary>
    private static long BucketUpperBound(int index)
    {
        if (index >= TotalBuckets - 1)
        {
            return 1L << OctaveCount;
        }
        return BucketLowerBound(index + 1);
    }

    /// <summary>
    /// Linearly interpolates within a bucket: how far is the target position
    /// (t) from the bucket's starting cumulative count, scaled across the
    /// bucket's microsecond width.
    /// </summary>
    private static long InterpolateBucket(int bucketIndex, double target, double cumulativeBefore, long count)
    {
        var lower = BucketLowerBound(bucketIndex);
        var upper = BucketUpperBound(bucketIndex);
        var width = upper - lower;
        if (width <= 0 || count <= 0)
        {
            return lower;
        }
        var fraction = (target - cumulativeBefore) / count;
        if (fraction < 0) fraction = 0;
        if (fraction > 1) fraction = 1;
        var value = lower + fraction * width;
        // Round to nearest integer microsecond.
        return (long)Math.Round(value);
    }
}
