using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Hosting;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Abstractions.Tests.Fakes;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Hosting;

public class InputHostConcurrencyTests
{
    private static DiscoveredDevice MakeDevice(string path, string serial) =>
        new(
            new DeviceIdentity(0x1038, 0x161C, serial, "SteelSeries", "Apex Pro"),
            path,
            SupportsAnalog: true);

    /// <summary>
    /// Observable contract: no matter how a selection change interleaves
    /// with a Drain call in flight, a key pressed on the previously selected
    /// device must never be left latched above zero once both complete.
    /// The filler events stretch the drain loop so a change landing between
    /// Drain's id snapshot and its store write is actually exercised.
    /// </summary>
    [Fact]
    public async Task Drain_overlapping_selection_change_never_leaves_a_key_latched()
    {
        var key = KeyId.FromScanCode(0x1E);
        const int iterations = 300;
        const int fillers = 4000;

        for (var i = 0; i < iterations; i++)
        {
            var ring = new SpscRingBuffer<RawKeyEvent>(8192);
            var raw = new FakeRawInputAdapter(ring);
            var devA = MakeDevice("dev://a", "SN-A");
            var devB = MakeDevice("dev://b", "SN-B");
            var enumerator = new InMemoryDeviceEnumerator(new[] { devA, devB });
            DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
            var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
            selector.Initialize();

            // Drain and the topology handlers race here, so the store must
            // be the concurrency-safe indexed variant.
            var store = new KeyStateStore(new KeyIndex(new[] { key }));

            await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
            await host.StartAsync(CancellationToken.None);
            raw.Push(new RawInputDeviceChanged(devB.Identity, Attached: true, devB.DevicePath, DeviceId: 8));
            raw.Push(new RawInputDeviceChanged(devA.Identity, Attached: true, devA.DevicePath, DeviceId: 7));
            selector.Select(devA);

            // Fillers from a never-selected unit, then the down under test.
            for (var f = 0; f < fillers; f++)
            {
                raw.Push(new RawKeyEvent(0x30, true, f, 9));
            }
            raw.Push(new RawKeyEvent(0x1E, true, fillers, 7));

            using var start = new ManualResetEventSlim(false);
            var spin = (i % 60) * 300; // scan the timing space across iterations
            var drain = Task.Run(() =>
            {
                start.Wait();
                host.Drain(8192);
            });
            var change = Task.Run(() =>
            {
                start.Wait();
                Thread.SpinWait(spin);
                selector.Select(devB);
            });
            start.Set();
            await Task.WhenAll(drain, change);

            store.Get(key).Value.Should().Be(
                0f, $"a selection change overlapping Drain must never leave a key latched (iteration {i})");
        }
    }
}
