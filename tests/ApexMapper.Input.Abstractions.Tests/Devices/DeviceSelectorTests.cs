using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Tests.Fakes;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Tests.Devices;

public class DeviceSelectorTests
{
    private static DiscoveredDevice Dev(
        string path,
        int vid = 0x1038,
        int pid = 0x161C,
        string? serial = null,
        bool analog = true)
        => new(
            new DeviceIdentity(vid, pid, serial ?? path, "SteelSeries", "Apex Pro"),
            path,
            analog);

    private sealed class EventRecorder
    {
        public List<DeviceTopologyChanged> Events { get; } = new();
        public void Handle(object? sender, DeviceTopologyChanged e) => Events.Add(e);
    }

    private sealed class SaveRecorder
    {
        public List<DeviceRegistry> Saves { get; } = new();
        public int Count => Saves.Count;
    }

    [Fact]
    public void Initialize_with_empty_enumerator_and_empty_registry_yields_no_devices_no_events()
    {
        var enumerator = new InMemoryDeviceEnumerator(Array.Empty<DiscoveredDevice>());
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var saves = new SaveRecorder();
        var recorder = new EventRecorder();

        var selector = new DeviceSelector(enumerator, () => registry, r => { registry = r; saves.Saves.Add(r); });
        selector.Changed += recorder.Handle;

        selector.Initialize();

        selector.Discovered.Should().BeEmpty();
        selector.SelectedDevice.Should().BeNull();
        selector.SelectedIdentity.Should().BeNull();
        recorder.Events.Should().BeEmpty();
        saves.Count.Should().Be(0);
    }

    [Fact]
    public void Initialize_auto_rebinds_silently_when_saved_identity_matches_discovered()
    {
        var a = Dev("a", serial: "SN-A");
        var b = Dev("b", serial: "SN-B");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a, b });
        DeviceRegistry registry = new(b.Identity, Array.Empty<KeyCalibration>());
        var recorder = new EventRecorder();

        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Changed += recorder.Handle;

        selector.Initialize();

        selector.Discovered.Should().Equal(a, b);
        selector.SelectedDevice.Should().Be(b);
        selector.SelectedIdentity.Should().Be(b.Identity);
        recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public void Initialize_does_not_auto_rebind_when_saved_identity_is_absent()
    {
        var a = Dev("a", serial: "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a });
        var missing = new DeviceIdentity(0x1038, 0x161C, "SN-Z", null, null);
        DeviceRegistry registry = new(missing, Array.Empty<KeyCalibration>());
        var recorder = new EventRecorder();

        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Changed += recorder.Handle;

        selector.Initialize();

        selector.SelectedDevice.Should().BeNull();
        recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public void Initialize_identity_match_honors_SerialNumber()
    {
        var a = Dev("a", serial: "SN-A");
        var b = Dev("b", serial: "SN-B");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a, b });
        DeviceRegistry registry = new(
            new DeviceIdentity(0x1038, 0x161C, "SN-B", null, null),
            Array.Empty<KeyCalibration>());

        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        selector.SelectedDevice.Should().Be(b);
    }

    [Fact]
    public void Refresh_after_attach_raises_attached_only_for_new_device()
    {
        var a = Dev("a", serial: "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());

        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        var b = Dev("b", serial: "SN-B");
        enumerator.Add(b);

        selector.Refresh();

        selector.Discovered.Should().Equal(a, b);
        recorder.Events.Should().ContainSingle()
            .Which.Should().Be(new DeviceTopologyChanged(DeviceTopologyChangeKind.Attached, b));
    }

    [Fact]
    public void Refresh_after_detach_of_selected_device_unselects_but_keeps_persisted_identity()
    {
        var a = Dev("a", serial: "SN-A");
        var b = Dev("b", serial: "SN-B");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a, b });
        var calibs = new[] { new KeyCalibration(new KeyId(0x1E), 0.1f, 0.9f, 0.01f) };
        DeviceRegistry registry = new(b.Identity, calibs);
        var saves = new SaveRecorder();

        var selector = new DeviceSelector(
            enumerator,
            () => registry,
            r => { registry = r; saves.Saves.Add(r); });
        selector.Initialize();

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        enumerator.Remove(b);
        selector.Refresh();

        selector.Discovered.Should().Equal(a);
        selector.SelectedDevice.Should().BeNull();
        recorder.Events.Should().HaveCount(2);
        recorder.Events[0].Should().Be(new DeviceTopologyChanged(DeviceTopologyChangeKind.Detached, b));
        recorder.Events[1].Should().Be(new DeviceTopologyChanged(DeviceTopologyChangeKind.Unselected, b));

        // An unplug must not erase the user's saved choice; only an explicit
        // Unselect does. No save happens at all.
        saves.Count.Should().Be(0);
        registry.SelectedDevice.Should().Be(b.Identity);
    }

    [Fact]
    public void Refresh_after_replug_of_saved_device_auto_reselects_and_raises_Selected()
    {
        var b = Dev("b", serial: "SN-B");
        var enumerator = new InMemoryDeviceEnumerator(new[] { b });
        DeviceRegistry registry = new(b.Identity, Array.Empty<KeyCalibration>());
        var saves = new SaveRecorder();

        var selector = new DeviceSelector(
            enumerator,
            () => registry,
            r => { registry = r; saves.Saves.Add(r); });
        selector.Initialize();
        selector.SelectedDevice.Should().Be(b);

        enumerator.Remove(b);
        selector.Refresh();
        selector.SelectedDevice.Should().BeNull();

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        enumerator.Add(b);
        selector.Refresh();

        selector.SelectedDevice.Should().Be(b);
        recorder.Events.Should().HaveCount(2);
        recorder.Events[0].Should().Be(new DeviceTopologyChanged(DeviceTopologyChangeKind.Attached, b));
        recorder.Events[1].Should().Be(new DeviceTopologyChanged(DeviceTopologyChangeKind.Selected, b));

        // The saved identity never changed, so nothing is re-persisted.
        saves.Count.Should().Be(0);
        registry.SelectedDevice.Should().Be(b.Identity);
    }

    [Fact]
    public void Refresh_reenumeration_with_new_path_rebinds_selection_in_one_pass()
    {
        // Sleep/resume can re-enumerate the same physical device under a new
        // path in a single refresh window.
        var b = Dev("b", serial: "SN-B");
        var enumerator = new InMemoryDeviceEnumerator(new[] { b });
        DeviceRegistry registry = new(b.Identity, Array.Empty<KeyCalibration>());

        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();
        selector.SelectedDevice.Should().Be(b);

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        var b2 = Dev("b-reborn", serial: "SN-B");
        enumerator.Remove(b);
        enumerator.Add(b2);
        selector.Refresh();

        selector.SelectedDevice.Should().Be(b2);
        recorder.Events.Should().Contain(new DeviceTopologyChanged(DeviceTopologyChangeKind.Selected, b2));
    }

    [Fact]
    public void Unselect_while_saved_device_is_absent_clears_persistence()
    {
        var a = Dev("a", serial: "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a });
        var missing = Dev("b", serial: "SN-B");
        var calibs = new[] { new KeyCalibration(new KeyId(0x1E), 0.1f, 0.9f, 0.01f) };
        DeviceRegistry registry = new(missing.Identity, calibs);
        var saves = new SaveRecorder();

        var selector = new DeviceSelector(
            enumerator,
            () => registry,
            r => { registry = r; saves.Saves.Add(r); });
        selector.Initialize();
        selector.SelectedDevice.Should().BeNull();

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        selector.Unselect();

        recorder.Events.Should().BeEmpty(); // no device instance to report
        saves.Count.Should().Be(1);
        saves.Saves[0].SelectedDevice.Should().BeNull();
        saves.Saves[0].Calibrations.Should().BeEquivalentTo(calibs);
    }

    [Fact]
    public void Refresh_after_detach_of_unselected_device_does_not_persist()
    {
        var a = Dev("a", serial: "SN-A");
        var b = Dev("b", serial: "SN-B");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a, b });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var saves = new SaveRecorder();

        var selector = new DeviceSelector(
            enumerator,
            () => registry,
            r => { registry = r; saves.Saves.Add(r); });
        selector.Initialize();

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        enumerator.Remove(a);
        selector.Refresh();

        recorder.Events.Should().ContainSingle()
            .Which.Should().Be(new DeviceTopologyChanged(DeviceTopologyChangeKind.Detached, a));
        saves.Count.Should().Be(0);
    }

    [Fact]
    public void Select_sets_selected_raises_event_and_persists_preserving_calibrations()
    {
        var a = Dev("a", serial: "SN-A");
        var b = Dev("b", serial: "SN-B");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a, b });
        var calibs = new[]
        {
            new KeyCalibration(new KeyId(0x1E), 0.1f, 0.9f, 0.01f),
            new KeyCalibration(new KeyId(0x30), 0.2f, 0.8f, 0.02f),
        };
        DeviceRegistry registry = new(null, calibs);
        var saves = new SaveRecorder();

        var selector = new DeviceSelector(
            enumerator,
            () => registry,
            r => { registry = r; saves.Saves.Add(r); });
        selector.Initialize();

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        selector.Select(b);

        selector.SelectedDevice.Should().Be(b);
        recorder.Events.Should().ContainSingle()
            .Which.Should().Be(new DeviceTopologyChanged(DeviceTopologyChangeKind.Selected, b));
        saves.Count.Should().Be(1);
        saves.Saves[0].SelectedDevice.Should().Be(b.Identity);
        saves.Saves[0].Calibrations.Should().BeEquivalentTo(calibs);
    }

    [Fact]
    public void Select_throws_when_device_is_not_in_discovered()
    {
        var a = Dev("a", serial: "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());

        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        var stranger = Dev("stranger", serial: "SN-X");
        Action act = () => selector.Select(stranger);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Unselect_clears_selected_raises_event_and_persists_null()
    {
        var a = Dev("a", serial: "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a });
        var calibs = new[] { new KeyCalibration(new KeyId(0x1E), 0.1f, 0.9f, 0.01f) };
        DeviceRegistry registry = new(a.Identity, calibs);
        var saves = new SaveRecorder();

        var selector = new DeviceSelector(
            enumerator,
            () => registry,
            r => { registry = r; saves.Saves.Add(r); });
        selector.Initialize();

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        selector.Unselect();

        selector.SelectedDevice.Should().BeNull();
        recorder.Events.Should().ContainSingle()
            .Which.Should().Be(new DeviceTopologyChanged(DeviceTopologyChangeKind.Unselected, a));
        saves.Count.Should().Be(1);
        saves.Saves[0].SelectedDevice.Should().BeNull();
        saves.Saves[0].Calibrations.Should().BeEquivalentTo(calibs);
    }

    [Fact]
    public void Unselect_when_nothing_selected_is_a_noop()
    {
        var a = Dev("a", serial: "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a });
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var saves = new SaveRecorder();

        var selector = new DeviceSelector(
            enumerator,
            () => registry,
            r => { registry = r; saves.Saves.Add(r); });
        selector.Initialize();

        var recorder = new EventRecorder();
        selector.Changed += recorder.Handle;

        selector.Unselect();

        recorder.Events.Should().BeEmpty();
        saves.Count.Should().Be(0);
    }

    [Fact]
    public void Calibrations_are_preserved_across_select_then_unselect_round_trip()
    {
        var a = Dev("a", serial: "SN-A");
        var enumerator = new InMemoryDeviceEnumerator(new[] { a });
        var calibs = new[]
        {
            new KeyCalibration(new KeyId(0x1E), 0.1f, 0.9f, 0.01f),
            new KeyCalibration(new KeyId(0x30), 0.2f, 0.8f, 0.02f),
        };
        DeviceRegistry registry = new(null, calibs);

        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        selector.Select(a);
        selector.Unselect();

        registry.SelectedDevice.Should().BeNull();
        registry.Calibrations.Should().BeEquivalentTo(calibs);
    }
}
