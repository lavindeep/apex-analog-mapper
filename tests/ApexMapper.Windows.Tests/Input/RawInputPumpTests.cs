using System.ComponentModel;
using System.Diagnostics;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Input;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

/// <summary>The ring, the cache and the registration constant: no thread, no window.</summary>
public class RawInputRingTests
{
    [Fact]
    public void The_registration_asks_for_input_from_every_window_plus_device_notifications()
    {
        Assert.Equal(0x2100u, RawInputPump.RegistrationFlags);
    }

    [Fact]
    public void The_ring_keeps_order_and_counts_overflow_instead_of_blocking()
    {
        using var pump = new RawInputPump();
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

        pump.Enqueue(new RawKeyEvent(new ScanCode(0x11), true, 99, 7));
        Assert.True(pump.TryDequeue(out var last));
        Assert.Equal(99, last.Device);
        Assert.Equal(RawInputPump.RingSize + 4, pump.EventCount);
        Assert.Equal(7, pump.LastEventTicks);
    }

    [Fact]
    public void A_device_change_forgets_the_cached_container_id_and_a_failed_lookup_is_never_cached()
    {
        using var pump = new RawInputPump();
        var tkl = new Guid("27373de1-4206-11f1-b9e4-14ac60fcc13e");
        pump.CacheForTest(0x1234, tkl);
        Assert.Equal(tkl, pump.ContainerIdOf(0x1234));

        pump.OnDeviceChanged(0x1234, arrived: false);

        Assert.False(pump.IsCached(0x1234));
        Assert.Null(pump.ContainerIdOf(0x1234));
        Assert.False(pump.IsCached(0x1234));
    }

    [Fact]
    public void A_throwing_device_handler_is_counted_and_never_escapes_the_window_procedure()
    {
        using var pump = new RawInputPump();
        var calls = 0;
        pump.DeviceChanged += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("handler bug");
        };

        pump.OnDeviceChanged(1, arrived: true);
        pump.OnDeviceChanged(2, arrived: false);

        Assert.Equal(2, calls);
        Assert.Equal(2, pump.HandlerFaults);
    }
}

[Collection(ProcessSingletons.Name)]
public class RawInputPumpTests
{
    [Fact]
    public void Stop_joins_within_half_a_second_and_clears_the_container_cache()
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
        pump.CacheForTest(0x1234, Guid.NewGuid());

        var clock = Stopwatch.StartNew();
        var stopped = pump.Stop();
        clock.Stop();

        Assert.True(stopped);
        Assert.True(clock.Elapsed.TotalMilliseconds < 500, $"Took {clock.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.False(pump.IsRunning);
        Assert.False(pump.IsCached(0x1234));
        Assert.Null(pump.Error);
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
        try
        {
            Assert.Throws<InvalidOperationException>(second.Start);
        }
        finally
        {
            first.Stop();
        }

        second.Start();
        Assert.True(second.Stop());
    }
}
