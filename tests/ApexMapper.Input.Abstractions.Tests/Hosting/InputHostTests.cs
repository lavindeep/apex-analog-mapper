using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Hosting;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Abstractions.Tests.Fakes;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Hosting;

public class InputHostTests
{
    private static DiscoveredDevice MakeDevice(string path = "test://device/1", string serial = "SN-1") =>
        new(
            new DeviceIdentity(0x1038, 0x161C, serial, "SteelSeries", "Apex Pro"),
            path,
            SupportsAnalog: true);

    private static DeviceSelector MakeSelector(params DiscoveredDevice[] devices)
    {
        var enumerator = new InMemoryDeviceEnumerator(devices);
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();
        return selector;
    }

    private static SpscRingBuffer<RawKeyEvent> MakeRing(int capacity = 256) => new(capacity);

    /// <summary>
    /// Simulates the adapter announcing the device (which binds its DeviceId)
    /// followed by the user selecting it.
    /// </summary>
    private static void AttachAndSelect(
        FakeRawInputAdapter raw,
        DeviceSelector selector,
        DiscoveredDevice device,
        int deviceId)
    {
        raw.Push(new RawInputDeviceChanged(device.Identity, Attached: true, device.DevicePath, deviceId));
        selector.Select(device);
    }

    private sealed class FaultingHidProbe : IHidAnalogProbe
    {
        private readonly Exception _failure;
        private readonly bool _failOnStart;

        public FaultingHidProbe(Exception failure, bool failOnStart = true)
        {
            _failure = failure;
            _failOnStart = failOnStart;
            Device = new DeviceIdentity(0x1234, 0x5678, "FAKE", "Fake", "Fake");
            Adapter = null!;
        }

        public DeviceIdentity Device { get; }
        public DeviceAdapterDescriptor Adapter { get; }
        public BackendStatus Status { get; private set; } = BackendStatus.Stopped;
        public bool IsHealthy => Status == BackendStatus.Running;
        public bool IsDisposed { get; private set; }

        public event EventHandler<BackendStatusChanged>? StatusChanged;

        public Task StartAsync(CancellationToken ct)
        {
            if (_failOnStart)
            {
                throw _failure;
            }
            Status = BackendStatus.Running;
            StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.HidAnalog, Status, null));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Status = BackendStatus.Stopped;
            StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.HidAnalog, Status, null));
            return Task.CompletedTask;
        }

        public IDisposable SubscribeRaw(KeyId key, Action<float> onRawNormalized) => new NoopDisposable();

        public void RaiseFault(string reason)
        {
            Status = BackendStatus.FaultedAnalog;
            StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.HidAnalog, Status, reason));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task Basic_compose_no_hid_probe_starts_digital_only()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var selector = MakeSelector();
        var store = new KeyStateStore();
        var log = new InMemoryLogSink();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store, log);

        await host.StartAsync(CancellationToken.None);

        host.DigitalStatus.Should().Be(BackendStatus.Running);
        host.AnalogStatus.Should().Be(BackendStatus.Stopped);
        host.AnalogFallbackReason.Should().BeNull();
    }

    [Fact]
    public async Task Drain_pushes_digital_keydown_from_selected_device_to_store()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var dev = MakeDevice();
        var selector = MakeSelector(dev);
        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, dev, deviceId: 7);

        var ev = new RawKeyEvent(ScanCode: 0x1E, IsDown: true, TimestampTicks: 1, DeviceId: 7);
        raw.Push(in ev).Should().BeTrue();

        var drained = host.Drain(10);

        drained.Should().Be(1);
        var state = store.Get(KeyId.FromScanCode(0x1E));
        state.Value.Should().Be(1.0f);
        state.Source.Should().Be(KeyProvenance.Digital);
    }

    [Fact]
    public async Task Drain_handles_key_release_back_to_zero()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var dev = MakeDevice();
        var selector = MakeSelector(dev);
        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, dev, deviceId: 7);

        var down = new RawKeyEvent(0x1E, IsDown: true, 1, 7);
        var up = new RawKeyEvent(0x1E, IsDown: false, 2, 7);
        raw.Push(in down);
        host.Drain(10);
        raw.Push(in up);
        host.Drain(10);

        var state = store.Get(KeyId.FromScanCode(0x1E));
        state.Value.Should().Be(0f);
        state.Source.Should().Be(KeyProvenance.Digital);
    }

    [Fact]
    public async Task Drain_drops_all_digital_events_when_no_device_is_selected()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var dev = MakeDevice();
        var selector = MakeSelector(dev); // discovered but never selected
        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        raw.Push(new RawInputDeviceChanged(dev.Identity, Attached: true, dev.DevicePath, DeviceId: 7));

        raw.Push(new RawKeyEvent(0x1E, true, 1, 7));
        var drained = host.Drain(10);

        drained.Should().Be(1);
        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(0f);
    }

    [Fact]
    public async Task Drain_drops_events_from_devices_other_than_the_selected_one()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var dev = MakeDevice();
        var selector = MakeSelector(dev);
        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, dev, deviceId: 7);

        // A second physical keyboard (never selected) must not drive mapping.
        raw.Push(new RawKeyEvent(0x1E, true, 1, 9));
        host.Drain(10);
        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(0f);

        // The selected device still does.
        raw.Push(new RawKeyEvent(0x1E, true, 2, 7));
        host.Drain(10);
        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(1f);
    }

    [Fact]
    public async Task Changing_selection_sweeps_keys_held_on_the_previous_device()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var devA = MakeDevice("dev://a", "SN-A");
        var devB = MakeDevice("dev://b", "SN-B");
        var selector = MakeSelector(devA, devB);
        var store = new KeyStateStore();
        var key = KeyId.FromScanCode(0x1E);

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        raw.Push(new RawInputDeviceChanged(devB.Identity, Attached: true, devB.DevicePath, DeviceId: 8));
        AttachAndSelect(raw, selector, devA, deviceId: 7);

        raw.Push(new RawKeyEvent(0x1E, true, 1, 7));
        host.Drain(10);
        store.Get(key).Value.Should().Be(1f);

        // Switching selection must not leave the old board's key latched.
        selector.Select(devB);
        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeTrue();

        // The old board's release is no longer ours to consume.
        raw.Push(new RawKeyEvent(0x1E, false, 2, 7));
        host.Drain(10);
        store.IsGated(key).Should().BeTrue();

        // The new board clears the gate with a full release, then drives output.
        raw.Push(new RawKeyEvent(0x1E, false, 3, 8));
        host.Drain(10);
        store.IsGated(key).Should().BeFalse();
        raw.Push(new RawKeyEvent(0x1E, true, 4, 8));
        host.Drain(10);
        store.Get(key).Value.Should().Be(1f);
    }

    [Fact]
    public async Task Unselecting_sweeps_held_keys_and_stops_all_digital_input()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var dev = MakeDevice();
        var selector = MakeSelector(dev);
        var store = new KeyStateStore();
        var key = KeyId.FromScanCode(0x1E);

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, dev, deviceId: 7);

        raw.Push(new RawKeyEvent(0x1E, true, 1, 7));
        host.Drain(10);
        store.Get(key).Value.Should().Be(1f);

        selector.Unselect();

        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeTrue();

        // With nothing selected the former device no longer drives mapping.
        raw.Push(new RawKeyEvent(0x30, true, 2, 7));
        host.Drain(10);
        store.Get(KeyId.FromScanCode(0x30)).Value.Should().Be(0f);
    }

    [Fact]
    public async Task Selection_restored_at_initialize_binds_device_id_from_adapter_arrival()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var dev = MakeDevice();
        var enumerator = new InMemoryDeviceEnumerator(new[] { dev });
        DeviceRegistry registry = new(dev.Identity, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();
        selector.SelectedDevice.Should().Be(dev); // silent rebind, no Selected event

        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);

        // The adapter announces the already-present device at startup; that
        // arrival must bind the selected device's id.
        raw.Push(new RawInputDeviceChanged(dev.Identity, Attached: true, dev.DevicePath, DeviceId: 5));

        raw.Push(new RawKeyEvent(0x1E, true, 1, 5));
        host.Drain(10);
        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(1f);
    }

    [Fact]
    public async Task Device_attach_gates_and_zeroes_currently_held_keys()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var devA = MakeDevice("dev://a", "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { devA });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, devA, deviceId: 1);

        // Hold a key
        var down = new RawKeyEvent(0x1E, IsDown: true, 1, 1);
        raw.Push(in down);
        host.Drain(10);

        // New device attaches → selector raises Attached
        var devB = MakeDevice("dev://b", "SN-B");
        enumerator.Add(devB);
        selector.Refresh();

        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(0f);
        store.IsGated(KeyId.FromScanCode(0x1E)).Should().BeTrue();
    }

    [Fact]
    public async Task Keyup_after_attach_gating_clears_gate_and_stays_zero()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var devA = MakeDevice("dev://a", "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { devA });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, devA, deviceId: 1);

        var down = new RawKeyEvent(0x1E, IsDown: true, 1, 1);
        raw.Push(in down);
        host.Drain(10);

        var devB = MakeDevice("dev://b", "SN-B");
        enumerator.Add(devB);
        selector.Refresh();

        store.IsGated(KeyId.FromScanCode(0x1E)).Should().BeTrue();

        // Now release the key — the store clears the gate on key-up.
        var up = new RawKeyEvent(0x1E, IsDown: false, 2, 1);
        raw.Push(in up);
        host.Drain(10);

        store.IsGated(KeyId.FromScanCode(0x1E)).Should().BeFalse();
        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(0f);
    }

    [Fact]
    public async Task Hid_probe_failure_on_start_logs_and_keeps_digital_running()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var selector = MakeSelector();
        var store = new KeyStateStore();
        var log = new InMemoryLogSink();
        var probe = new FaultingHidProbe(new IOException("analog probe blocked by gg"));

        await using var host = new InputHost(raw, probe, selector, ring, store, log);

        // Must not throw.
        await host.StartAsync(CancellationToken.None);

        host.DigitalStatus.Should().Be(BackendStatus.Running);
        host.AnalogStatus.Should().Be(BackendStatus.FaultedAnalog);
        host.AnalogFallbackReason.Should().Be("analog probe blocked by gg");
        log.Lines.Should().Contain(line =>
            line.Contains("analog probe blocked: analog probe blocked by gg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hid_probe_failure_raises_StatusChanged_with_FaultedAnalog()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var selector = MakeSelector();
        var store = new KeyStateStore();
        var probe = new FaultingHidProbe(new IOException("kaboom"));
        var events = new List<BackendStatusChanged>();

        await using var host = new InputHost(raw, probe, selector, ring, store);
        host.StatusChanged += (_, e) => events.Add(e);

        await host.StartAsync(CancellationToken.None);

        events.Should().Contain(e => e.Kind == BackendKind.HidAnalog && e.Status == BackendStatus.FaultedAnalog);
    }

    [Fact]
    public async Task Hid_probe_status_transitions_forwarded_to_host()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var selector = MakeSelector();
        var store = new KeyStateStore();
        var probe = new FaultingHidProbe(new IOException("late"), failOnStart: false);
        var events = new List<BackendStatusChanged>();

        await using var host = new InputHost(raw, probe, selector, ring, store);
        host.StatusChanged += (_, e) => events.Add(e);

        await host.StartAsync(CancellationToken.None);
        host.AnalogStatus.Should().Be(BackendStatus.Running);

        probe.RaiseFault("device dropped");

        host.AnalogStatus.Should().Be(BackendStatus.FaultedAnalog);
        host.DigitalStatus.Should().Be(BackendStatus.Running);
        events.Should().Contain(e =>
            e.Kind == BackendKind.HidAnalog && e.Status == BackendStatus.FaultedAnalog && e.Reason == "device dropped");
    }

    [Fact]
    public async Task StartAsync_does_not_throw_when_hid_probe_fails()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var selector = MakeSelector();
        var store = new KeyStateStore();
        var probe = new FaultingHidProbe(new IOException("gg"));

        await using var host = new InputHost(raw, probe, selector, ring, store);

        Func<Task> act = () => host.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Gated_key_ignores_auto_repeat_downs_until_released()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var devA = MakeDevice("dev://a", "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { devA });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        var store = new KeyStateStore();

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, devA, deviceId: 1);

        // Press a key, attach a new device → key gets ignored.
        raw.Push(new RawKeyEvent(0x1E, true, 1, 1));
        host.Drain(10);

        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(1f);

        var devB = MakeDevice("dev://b", "SN-B");
        enumerator.Add(devB);
        selector.Refresh();

        // Gating must zero the held key immediately.
        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(0f);

        // Synthetic "down repeat" coming in while gated must not re-press.
        // (No real keyup yet — gate still active.)
        raw.Push(new RawKeyEvent(0x1E, true, 3, 1));
        host.Drain(10);

        store.Get(KeyId.FromScanCode(0x1E)).Value.Should().Be(0f);
    }

    [Fact]
    public async Task Held_key_across_attach_gate_full_sequence_recovers_after_release()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var devA = MakeDevice("dev://a", "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { devA });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        var store = new KeyStateStore();
        var key = KeyId.FromScanCode(0x1E);

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, devA, deviceId: 1);

        // Hold at 1.0.
        raw.Push(new RawKeyEvent(0x1E, true, 1, 1));
        host.Drain(10);
        store.Get(key).Value.Should().Be(1f);

        // Gate transition (device attach) zeroes the held key.
        enumerator.Add(MakeDevice("dev://b", "SN-B"));
        selector.Refresh();
        store.Get(key).Value.Should().Be(0f);

        // Physical release: stays zero and the gate clears.
        raw.Push(new RawKeyEvent(0x1E, false, 2, 1));
        host.Drain(10);
        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeFalse();

        // Next press drives output again.
        raw.Push(new RawKeyEvent(0x1E, true, 3, 1));
        host.Drain(10);
        store.Get(key).Value.Should().Be(1f);
    }

    [Fact]
    public async Task Device_detach_sweeps_held_keys_to_zero()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var devA = MakeDevice("dev://a", "SN-A");
        var selector = MakeSelector(devA);
        var store = new KeyStateStore();
        var key = KeyId.FromScanCode(0x1E);

        await using var host = new InputHost(raw, hidProbe: null, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, devA, deviceId: 1);

        raw.Push(new RawKeyEvent(0x1E, true, 1, 1));
        host.Drain(10);
        store.Get(key).Value.Should().Be(1f);

        // Unplug mid-press: the held key must not stay latched at full.
        raw.Push(new RawInputDeviceChanged(devA.Identity, Attached: false, devA.DevicePath, DeviceId: 1));

        store.Get(key).Value.Should().Be(0f);
        store.IsGated(key).Should().BeTrue();

        // A subsequent auto-repeat down must not re-press.
        raw.Push(new RawKeyEvent(0x1E, true, 2, 1));
        host.Drain(10);
        store.Get(key).Value.Should().Be(0f);
    }

    [Fact]
    public async Task FaultedAnalog_sweeps_analog_keys_but_leaves_digital_keys_alone()
    {
        var ring = MakeRing();
        var raw = new FakeRawInputAdapter(ring);
        var dev = MakeDevice();
        var selector = MakeSelector(dev);
        var store = new KeyStateStore();
        var probe = new FaultingHidProbe(new IOException("late"), failOnStart: false);
        var analogKey = KeyId.FromScanCode(0x11);
        var digitalKey = KeyId.FromScanCode(0x1E);

        await using var host = new InputHost(raw, probe, selector, ring, store);
        await host.StartAsync(CancellationToken.None);
        AttachAndSelect(raw, selector, dev, deviceId: 3);

        store.Set(analogKey, 0.8f, KeyProvenance.Analog);
        raw.Push(new RawKeyEvent(0x1E, true, 1, 3));
        host.Drain(10);

        // Mid-session HID fault must not leave stale analog depths behind.
        probe.RaiseFault("device dropped");

        store.Get(analogKey).Value.Should().Be(0f);
        store.IsGated(analogKey).Should().BeTrue();
        store.Get(digitalKey).Value.Should().Be(1f);
        store.IsGated(digitalKey).Should().BeFalse();
    }
}
