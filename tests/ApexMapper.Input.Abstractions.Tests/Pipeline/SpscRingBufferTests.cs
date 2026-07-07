using ApexMapper.Input.Abstractions.Pipeline;

namespace ApexMapper.Input.Abstractions.Tests.Pipeline;

public class SpscRingBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(-4)]
    public void Ctor_rejects_non_power_of_two_or_too_small(int capacity)
    {
        var act = () => new SpscRingBuffer<RawKeyEvent>(capacity);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(1024)]
    public void Ctor_accepts_power_of_two_capacities(int capacity)
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(capacity);

        ring.Capacity.Should().Be(capacity);
        ring.IsEmpty.Should().BeTrue();
        ring.Count.Should().Be(0);
        ring.DroppedCount.Should().Be(0);
    }

    [Fact]
    public void Empty_buffer_dequeue_returns_false()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(4);

        ring.TryDequeue(out var evt).Should().BeFalse();
        evt.Should().Be(default(RawKeyEvent));
        ring.IsEmpty.Should().BeTrue();
        ring.Count.Should().Be(0);
    }

    [Fact]
    public void Single_enqueue_then_dequeue_returns_same_event()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(4);
        var input = new RawKeyEvent(0xE04D, true, 42L, 0x0BADF00D);

        ring.TryEnqueue(in input).Should().BeTrue();
        ring.Count.Should().Be(1);
        ring.IsEmpty.Should().BeFalse();

        ring.TryDequeue(out var output).Should().BeTrue();
        output.Should().Be(input);
        output.DeviceId.Should().Be(0x0BADF00D);
        ring.IsEmpty.Should().BeTrue();
        ring.Count.Should().Be(0);
    }

    [Fact]
    public void Fill_to_capacity_then_drop_newest_on_overflow()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(4);

        for (var i = 0; i < 4; i++)
        {
            var evt = new RawKeyEvent((ushort)i, true, i, 0);
            ring.TryEnqueue(in evt).Should().BeTrue();
        }

        ring.Count.Should().Be(4);

        var overflow = new RawKeyEvent(99, false, 999L, 0);
        ring.TryEnqueue(in overflow).Should().BeFalse();
        ring.DroppedCount.Should().Be(1);

        var overflow2 = new RawKeyEvent(100, false, 1000L, 0);
        ring.TryEnqueue(in overflow2).Should().BeFalse();
        ring.DroppedCount.Should().Be(2);
    }

    [Fact]
    public void Drain_restores_capacity_for_subsequent_enqueues()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(4);

        for (var i = 0; i < 4; i++)
        {
            var evt = new RawKeyEvent((ushort)i, true, i, 0);
            ring.TryEnqueue(in evt).Should().BeTrue();
        }

        var overflow = new RawKeyEvent(99, false, 999L, 0);
        ring.TryEnqueue(in overflow).Should().BeFalse();
        ring.DroppedCount.Should().Be(1);

        for (var i = 0; i < 4; i++)
        {
            ring.TryDequeue(out var _).Should().BeTrue();
        }

        ring.IsEmpty.Should().BeTrue();

        for (var i = 0; i < 4; i++)
        {
            var evt = new RawKeyEvent((ushort)(i + 10), true, i + 10, 0);
            ring.TryEnqueue(in evt).Should().BeTrue();
        }

        ring.Count.Should().Be(4);
        ring.DroppedCount.Should().Be(1);
    }

    [Fact]
    public void Wrap_around_preserves_FIFO_ordering()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(4);

        for (var i = 0; i < 3; i++)
        {
            var evt = new RawKeyEvent((ushort)i, true, i, 0);
            ring.TryEnqueue(in evt).Should().BeTrue();
        }

        ring.TryDequeue(out var d0).Should().BeTrue();
        d0.TimestampTicks.Should().Be(0L);
        ring.TryDequeue(out var d1).Should().BeTrue();
        d1.TimestampTicks.Should().Be(1L);

        for (var i = 3; i < 6; i++)
        {
            var evt = new RawKeyEvent((ushort)i, true, i, 0);
            ring.TryEnqueue(in evt).Should().BeTrue();
        }

        ring.Count.Should().Be(4);

        var observed = new List<long>();
        while (ring.TryDequeue(out var evt))
        {
            observed.Add(evt.TimestampTicks);
        }

        observed.Should().Equal(2L, 3L, 4L, 5L);
        ring.DroppedCount.Should().Be(0);
    }

    [Fact]
    public void Two_thread_fuzz_preserves_order_and_accounts_for_all_events()
    {
        const int total = 100_000;
        var ring = new SpscRingBuffer<RawKeyEvent>(1024);
        var consumed = new List<long>(total);

        var producerDone = false;

        var producer = new Thread(() =>
        {
            for (long i = 0; i < total; i++)
            {
                var evt = new RawKeyEvent(0, true, i, 0);
                while (!ring.TryEnqueue(in evt))
                {
                    if (ring.DroppedCount > 0)
                    {
                        break;
                    }
                    Thread.SpinWait(1);
                }
            }
            Volatile.Write(ref producerDone, true);
        });

        var consumer = new Thread(() =>
        {
            while (true)
            {
                if (ring.TryDequeue(out var evt))
                {
                    consumed.Add(evt.TimestampTicks);
                    continue;
                }

                if (Volatile.Read(ref producerDone) && ring.IsEmpty)
                {
                    return;
                }

                Thread.SpinWait(1);
            }
        });

        producer.Start();
        consumer.Start();

        // Generous scheduling allowance, deliberately decoupled from the
        // properties under test: a saturated shared CI runner can stretch two
        // spinning threads well past any tight wall-clock guess (a 5s bound
        // flaked at ~9s on a loaded runner), while a genuinely broken ring
        // fails the ordering/accounting asserts below at any speed and a
        // wedged ring never joins at all.
        producer.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the producer must eventually finish");
        consumer.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the consumer must eventually finish");

        for (var i = 1; i < consumed.Count; i++)
        {
            consumed[i].Should().BeGreaterThan(consumed[i - 1]);
        }

        ((long)consumed.Count + ring.DroppedCount).Should().Be(total);
    }
}
