using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Hosting;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Abstractions.Tests.Fakes;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Hosting;

public class InputHostAllocationTests
{
    [Fact]
    public async Task Steady_state_drain_does_not_allocate()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(256);
        var raw = new FakeRawInputAdapter(ring);
        var dev = new DiscoveredDevice(
            new DeviceIdentity(0x1038, 0x161C, "SN-1", "SteelSeries", "Apex Pro"),
            "dev://a",
            SupportsAnalog: true);
        var enumerator = new InMemoryDeviceEnumerator(new[] { dev });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();
        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        raw.Push(new RawInputDeviceChanged(dev.Identity, Attached: true, dev.DevicePath, DeviceId: 7));
        selector.Select(dev);

        var down = new RawKeyEvent(0x1E, IsDown: true, 1, 7);
        var up = new RawKeyEvent(0x1E, IsDown: false, 2, 7);

        for (var i = 0; i < 1_000; i++)
        {
            raw.Push(in down);
            raw.Push(in up);
            host.Drain(8);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            raw.Push(in down);
            raw.Push(in up);
            host.Drain(8);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0, "InputHost.Drain must not allocate after warm-up");
    }
}
