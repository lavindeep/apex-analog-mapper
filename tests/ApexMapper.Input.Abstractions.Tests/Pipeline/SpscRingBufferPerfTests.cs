using System.Diagnostics;
using ApexMapper.Input.Abstractions.Pipeline;

namespace ApexMapper.Input.Abstractions.Tests.Pipeline;

[Trait("perf", "true")]
public class SpscRingBufferPerfTests
{
    [Fact]
    public void enqueue_dequeue_p99_under_50us_for_100k_ops()
    {
        const int ops = 100_000;
        var ring = new SpscRingBuffer<RawKeyEvent>(1024);
        var latencies = new long[ops];

        for (int i = 0; i < 1000; i++)
        {
            var warm = new RawKeyEvent((ushort)i, true, i, 0);
            ring.TryEnqueue(in warm);
            ring.TryDequeue(out _);
        }

        for (int i = 0; i < ops; i++)
        {
            var evt = new RawKeyEvent((ushort)i, true, i, 0);
            var start = Stopwatch.GetTimestamp();
            ring.TryEnqueue(in evt);
            ring.TryDequeue(out _);
            latencies[i] = Stopwatch.GetTimestamp() - start;
        }

        Array.Sort(latencies);
        var p99Ticks = latencies[(int)(ops * 0.99)];
        var p99Microseconds = p99Ticks * 1_000_000.0 / Stopwatch.Frequency;

        p99Microseconds.Should().BeLessThan(50.0, $"p99 = {p99Microseconds:F2}us");
    }
}
