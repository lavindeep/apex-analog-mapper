using System.ComponentModel;
using System.Diagnostics;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Input;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

[Collection(ProcessSingletons.Name)]
public class RawInputPumpTests
{
    [Fact]
    public void The_ring_keeps_order_and_counts_overflow_instead_of_blocking()
    {
        var pump = new RawInputPump();
        for (var i = 1; i <= RawInputPump.RingSize + 3; i++)
        {
            pump.Enqueue(new RawKeyEvent(new ScanCode((ushort)(i % 200 + 1)), i % 2 == 0, i, i));
        }

        Assert.Equal(3, pump.Overflows);
        var seen = 0;
        while (pump.TryDequeue(out var item))
        {
            seen++;
            Assert.Equal(seen, item.Device);
        }
        Assert.Equal(RawInputPump.RingSize, seen);
        Assert.False(pump.TryDequeue(out _));

        pump.Enqueue(new RawKeyEvent(new ScanCode(0x11), true, 99, 0));
        Assert.True(pump.TryDequeue(out var last));
        Assert.Equal(99, last.Device);
    }

    [Fact]
    public void Stop_joins_within_half_a_second()
    {
        using var pump = new RawInputPump();
        try
        {
            pump.Start();
        }
        catch (Win32Exception e)
        {
            Assert.Skip("This session cannot register a Raw Input window: " + e.Message);
            return;
        }
        Assert.True(pump.IsRunning);

        var clock = Stopwatch.StartNew();
        pump.Stop();
        clock.Stop();

        Assert.True(clock.Elapsed.TotalMilliseconds < 500, $"Took {clock.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.False(pump.IsRunning);
    }

    [Fact]
    public void A_second_pump_in_the_process_is_refused_while_the_first_runs()
    {
        using var first = new RawInputPump();
        try
        {
            first.Start();
        }
        catch (Win32Exception e)
        {
            Assert.Skip("This session cannot register a Raw Input window: " + e.Message);
            return;
        }
        using var second = new RawInputPump();

        Assert.Throws<InvalidOperationException>(second.Start);
        first.Stop();
        second.Start();
        second.Stop();
    }
}
