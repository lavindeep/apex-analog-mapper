using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Abstractions.Tests.Fakes;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Fakes;

public class FakeRawInputAdapterTests
{
    private static DeviceIdentity Id() => new(0x1038, 0x161C, "SN", "SteelSeries", "Apex Pro");

    [Fact]
    public async Task StartAsync_flips_status_to_Running_and_raises_event()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(8);
        var fake = new FakeRawInputAdapter(ring);
        BackendStatusChanged? captured = null;
        fake.StatusChanged += (_, e) => captured = e;

        fake.Status.Should().Be(BackendStatus.Stopped);
        await fake.StartAsync(CancellationToken.None);

        fake.Status.Should().Be(BackendStatus.Running);
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(BackendKind.RawInput);
        captured.Status.Should().Be(BackendStatus.Running);
    }

    [Fact]
    public async Task StopAsync_flips_status_to_Stopped_and_raises_event()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(8);
        var fake = new FakeRawInputAdapter(ring);
        await fake.StartAsync(CancellationToken.None);

        BackendStatusChanged? captured = null;
        fake.StatusChanged += (_, e) => captured = e;
        await fake.StopAsync(CancellationToken.None);

        fake.Status.Should().Be(BackendStatus.Stopped);
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(BackendStatus.Stopped);
    }

    [Fact]
    public async Task Push_RawKeyEvent_enqueues_into_supplied_ring()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(8);
        var fake = new FakeRawInputAdapter(ring);
        await fake.StartAsync(CancellationToken.None);

        var ev = new RawKeyEvent(ScanCode: 0x001E, IsDown: true, TimestampTicks: 123L, DeviceHandleIndex: 0);
        fake.Push(ev);

        ring.TryDequeue(out var dequeued).Should().BeTrue();
        dequeued.Should().Be(ev);
    }

    [Fact]
    public void Push_DeviceChanged_fires_event_with_payload()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(8);
        var fake = new FakeRawInputAdapter(ring);
        RawInputDeviceChanged? captured = null;
        fake.DeviceChanged += (_, e) => captured = e;

        var change = new RawInputDeviceChanged(Id(), Attached: true, DevicePath: @"\\?\path");
        fake.Push(change);

        captured.Should().Be(change);
    }

    [Fact]
    public async Task DisposeAsync_marks_disposed()
    {
        var ring = new SpscRingBuffer<RawKeyEvent>(8);
        var fake = new FakeRawInputAdapter(ring);
        await fake.DisposeAsync();

        fake.IsDisposed.Should().BeTrue();
    }
}
