using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Hosting;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Abstractions.Tests.Fakes;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Hosting;

public class InputHostAnalogSourceTests
{
    private static readonly KeyId AnalogKey = KeyId.FromScanCode(0x1E);
    private static readonly KeyId DigitalKey = KeyId.FromScanCode(0x30);

    [Fact]
    public async Task Analog_depth_survives_raw_repeat_and_release_and_applies_on_empty_ticks()
    {
        await using var fixture = new Fixture();
        fixture.SelectFirst();
        fixture.Analog.Depth = 0.4f;
        fixture.Host.Drain(8).Should().Be(0);
        fixture.Store.Get(AnalogKey).Value.Should().Be(0.4f);

        foreach (var down in new[] { true, true, false })
        {
            fixture.Raw.Push(new RawKeyEvent(0x1E, down, 0, 7));
            fixture.Host.Drain(8).Should().Be(1);
            fixture.Analog.ValueBeforeApply.Should().Be(0.4f);
            fixture.Store.Get(AnalogKey).Value.Should().Be(0.4f);
        }
        fixture.Analog.ApplyCount.Should().Be(4);
    }

    [Fact]
    public async Task Raw_release_cannot_clear_an_analog_held_key_gate()
    {
        await using var fixture = new Fixture();
        fixture.SelectFirst();
        fixture.Analog.Depth = 0.4f;
        fixture.Host.Drain(8);
        fixture.Store.GateHeldKeys();
        fixture.Raw.Push(new RawKeyEvent(0x1E, false, 0, 7));
        fixture.Host.Drain(8);
        fixture.Store.Get(AnalogKey).Value.Should().Be(0f);
    }

    [Fact]
    public async Task Startup_selection_retargets_and_unselection_clears_source_before_gating()
    {
        await using var fixture = new Fixture();
        fixture.SelectFirst();
        await fixture.Host.StartAsync(CancellationToken.None);
        fixture.Analog.SelectedAtStart.Should().Be(fixture.First);
        fixture.Analog.Depth = 0.4f;
        fixture.Host.Drain(8);
        fixture.Analog.OnSelect = _ => fixture.Store.Get(AnalogKey).Value.Should().Be(0.4f);
        fixture.Selector.Select(fixture.Second);
        fixture.Analog.Selected.Should().Be(fixture.Second);
        fixture.Store.Get(AnalogKey).Value.Should().Be(0f);
        fixture.Analog.OnSelect = null;
        fixture.Selector.Unselect();
        fixture.Analog.Selected.Should().BeNull();
    }

    [Fact]
    public async Task Selected_raw_removal_disables_source_despite_lagging_enumerator()
    {
        await using var fixture = new Fixture();
        fixture.SelectFirst();
        fixture.Raw.Push(new RawInputDeviceChanged(fixture.First.Identity, false, "", 7));
        fixture.Selector.SelectedDevice.Should().Be(fixture.First);
        fixture.Analog.Selected.Should().BeNull();
        var selections = fixture.Analog.SelectionCount;
        fixture.Raw.Push(new RawInputDeviceChanged(fixture.Second.Identity, true, fixture.Second.DevicePath, 8));
        fixture.Analog.SelectionCount.Should().Be(selections);
        fixture.Analog.Selected.Should().BeNull();
        fixture.Raw.Push(new RawInputDeviceChanged(fixture.First.Identity, true, fixture.First.DevicePath, 9));
        fixture.Analog.Selected.Should().Be(fixture.First);
    }

    [Fact]
    public async Task Unrelated_attach_preserves_analog_depth_and_wrong_keyboard_is_ignored()
    {
        await using var fixture = new Fixture();
        fixture.SelectFirst();
        fixture.Analog.Depth = 0.4f;
        fixture.Host.Drain(8);
        var selections = fixture.Analog.SelectionCount;
        var third = Device("third", "SN3");
        fixture.Enumerator.Add(third);
        fixture.Raw.Push(new RawInputDeviceChanged(third.Identity, true, third.DevicePath, 10));
        fixture.Analog.SelectionCount.Should().Be(selections);
        fixture.Store.Get(AnalogKey).Value.Should().Be(0.4f);
        fixture.Raw.Push(new RawKeyEvent(0x30, true, 0, 10));
        fixture.Host.Drain(8);
        fixture.Store.Get(DigitalKey).Value.Should().Be(0f);
        fixture.Raw.Push(new RawKeyEvent(0x30, true, 0, 7));
        fixture.Host.Drain(8);
        fixture.Store.Get(DigitalKey).Value.Should().Be(1f);
    }

    [Fact]
    public async Task Removal_during_selection_ignores_the_previous_raw_device_id()
    {
        var first = Device("first", "SN1");
        var second = Device("second", "SN2");
        var enumerator = new InMemoryDeviceEnumerator(new[] { first, second });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, value => registry = value);
        selector.Initialize();
        var ring = new SpscRingBuffer<RawKeyEvent>(32);
        var raw = new FakeRawInputAdapter(ring);
        var analog = new FakeAnalogSource();
        var store = new KeyStateStore(new KeyIndex(new[] { AnalogKey }));

        // Enter removal after SelectedDevice changes but before the host's
        // selection callback publishes its new raw id, as a lock race can do.
        selector.Changed += (_, change) =>
        {
            if (change.ChangeKind != DeviceTopologyChangeKind.Selected || change.Device != second) return;
            raw.Push(new RawInputDeviceChanged(first.Identity, false, "", 7));
            analog.Selected.Should().Be(first, "removing the old keyboard must not stop the source for the new selection");
        };
        await using var host = new InputHost(raw, null, selector, ring, store, analogInput: analog);
        raw.Push(new RawInputDeviceChanged(first.Identity, true, first.DevicePath, 7));
        raw.Push(new RawInputDeviceChanged(second.Identity, true, second.DevicePath, 8));
        selector.Select(first);

        selector.Select(second);

        analog.Selected.Should().Be(second);
        analog.Depth = 0.4f;
        host.Drain(8);
        store.Get(AnalogKey).Value.Should().Be(0.4f);
    }

    [Theory]
    [InlineData(BackendStatus.Stopped)]
    [InlineData(BackendStatus.FaultedAnalog)]
    public async Task Source_terminal_status_gates_analog_depth_and_is_forwarded(BackendStatus status)
    {
        await using var fixture = new Fixture();
        fixture.SelectFirst();
        fixture.Analog.Depth = 0.4f;
        fixture.Host.Drain(8);
        BackendStatusChanged? received = null;
        fixture.Host.StatusChanged += (_, change) => received = change;
        fixture.Analog.Raise(status);
        fixture.Store.Get(AnalogKey).Value.Should().Be(0f);
        fixture.Host.AnalogStatus.Should().Be(status);
        received!.Status.Should().Be(status);
        fixture.Host.AnalogReadinessError.Should().Be("test readiness");
    }

    private static DiscoveredDevice Device(string path, string serial) => new(
        new DeviceIdentity(0x1038, 0x161C, serial, "SteelSeries", "Apex Pro"), path, true);

    private sealed class Fixture : IAsyncDisposable
    {
        public DiscoveredDevice First { get; } = Device("first", "SN1");
        public DiscoveredDevice Second { get; } = Device("second", "SN2");
        public InMemoryDeviceEnumerator Enumerator { get; }
        public DeviceSelector Selector { get; }
        public FakeRawInputAdapter Raw { get; }
        public FakeAnalogSource Analog { get; } = new();
        public KeyStateStore Store { get; } = new(new KeyIndex(new[] { AnalogKey, DigitalKey }));
        public InputHost Host { get; }

        public Fixture()
        {
            Enumerator = new(new[] { First, Second });
            DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
            Selector = new(Enumerator, () => registry, value => registry = value);
            Selector.Initialize();
            var ring = new SpscRingBuffer<RawKeyEvent>(32);
            Raw = new(ring);
            Host = new(Raw, null, Selector, ring, Store, analogInput: Analog);
        }

        public void SelectFirst()
        {
            Raw.Push(new RawInputDeviceChanged(First.Identity, true, First.DevicePath, 7));
            Selector.Select(First);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class FakeAnalogSource : IAnalogInputSource
    {
        public BackendStatus Status { get; private set; } = BackendStatus.Stopped;
        public string? ReadinessError => "test readiness";
        public DiscoveredDevice? Selected { get; private set; }
        public DiscoveredDevice? SelectedAtStart { get; private set; }
        public Action<DiscoveredDevice?>? OnSelect { get; set; }
        public float Depth { get; set; }
        public float ValueBeforeApply { get; private set; }
        public int ApplyCount { get; private set; }
        public int SelectionCount { get; private set; }
        public event EventHandler<BackendStatusChanged>? StatusChanged;

        public void SelectDevice(DiscoveredDevice? device)
        {
            OnSelect?.Invoke(device);
            Selected = device;
            SelectionCount++;
        }

        public bool OwnsKey(KeyId key) => key == AnalogKey;
        public void ApplyTo(KeyStateStore store)
        {
            ApplyCount++;
            ValueBeforeApply = store.Get(AnalogKey).Value;
            if (Selected is not null) store.Set(AnalogKey, Depth, KeyProvenance.Analog);
        }

        public Task StartAsync(CancellationToken ct)
        {
            SelectedAtStart = Selected;
            Raise(BackendStatus.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Raise(BackendStatus.Stopped);
            return Task.CompletedTask;
        }

        public void Raise(BackendStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, new(BackendKind.HidAnalog, status, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
