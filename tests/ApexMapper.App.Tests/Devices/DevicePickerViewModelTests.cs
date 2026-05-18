using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels.Devices;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Devices;

public sealed class DevicePickerViewModelTests
{
    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    private sealed class FakeSelector : IDeviceSelectorFacade
    {
        private readonly List<DeviceFacadeEntry> _entries = new();
        public Guid? PrimaryId { get; private set; }
        public List<Guid> SelectPrimaryCalls { get; } = new();

        public event EventHandler<TopologyChangedEventArgs>? TopologyChanged;

        public void AddEntry(DeviceFacadeEntry entry) => _entries.Add(entry);

        public void SetPrimary(Guid id)
        {
            PrimaryId = id;
            _entries.ForEach(e =>
            {
                var idx = _entries.IndexOf(e);
                _entries[idx] = e with { IsPrimary = e.Id == id };
            });
        }

        public void ReplaceEntries(IEnumerable<DeviceFacadeEntry> entries)
        {
            _entries.Clear();
            _entries.AddRange(entries);
        }

        public IReadOnlyList<DeviceFacadeEntry> ListAll() => _entries.AsReadOnly();

        public void SelectPrimary(Guid id)
        {
            SelectPrimaryCalls.Add(id);
            SetPrimary(id);
        }

        public void Refresh() { /* No-op; tests manipulate state directly */ }

        public void FireTopologyChanged(IReadOnlyList<DeviceFacadeEntry> devices)
            => TopologyChanged?.Invoke(this, new TopologyChangedEventArgs(devices));
    }

    private sealed class FakeRegistry : IDeviceRegistryFacade
    {
        private readonly Dictionary<Guid, DeviceCalibrationStatus> _statuses = new();

        public void SetStatus(Guid id, DeviceCalibrationStatus status)
            => _statuses[id] = status;

        public DeviceCalibrationStatus GetStatus(Guid id)
            => _statuses.TryGetValue(id, out var s) ? s : DeviceCalibrationStatus.Unknown;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static DeviceFacadeEntry MakeEntry(
        Guid id,
        string name = "Test Device",
        ushort vid = 0x1038,
        ushort pid = 0x1610,
        bool isConnected = true,
        bool isPrimary = false) =>
        new(id, name, vid, pid, isConnected, isPrimary);

    private static DevicePickerViewModel BuildVm(
        FakeSelector selector,
        FakeRegistry? registry = null) =>
        new(selector, registry ?? new FakeRegistry());

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void Constructor_loads_devices_from_selector()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var selector = new FakeSelector();
        selector.AddEntry(MakeEntry(id1, "Apex Pro 1", isPrimary: true));
        selector.AddEntry(MakeEntry(id2, "Apex Pro 2"));

        var vm = BuildVm(selector);

        vm.Devices.Should().HaveCount(2);
        vm.Devices.Should().Contain(d => d.Id == id1 && d.IsPrimary);
        vm.Devices.Should().Contain(d => d.Id == id2 && !d.IsPrimary);
        vm.Primary.Should().NotBeNull();
        vm.Primary!.Id.Should().Be(id1);
    }

    [Fact]
    public void TopologyChanged_event_replaces_devices_list()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var idNew = Guid.NewGuid();
        var selector = new FakeSelector();
        selector.AddEntry(MakeEntry(id1));
        selector.AddEntry(MakeEntry(id2));

        var vm = BuildVm(selector);
        vm.Devices.Should().HaveCount(2);

        var propertyChanges = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        // Fire event with a new topology: id2 gone, idNew added
        selector.FireTopologyChanged(new[]
        {
            MakeEntry(id1),
            MakeEntry(idNew, "New Device"),
        });

        // idNew should be present; id2 should remain but disconnected
        vm.Devices.Should().Contain(d => d.Id == id1);
        vm.Devices.Should().Contain(d => d.Id == idNew);
        var id2Row = vm.Devices.FirstOrDefault(d => d.Id == id2);
        id2Row.Should().NotBeNull("disconnected rows must be preserved");
        id2Row!.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void MakePrimaryCommand_calls_selector_and_refreshes()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var selector = new FakeSelector();
        selector.AddEntry(MakeEntry(id1, isPrimary: true));
        selector.AddEntry(MakeEntry(id2));

        var vm = BuildVm(selector);

        vm.MakePrimaryCommand.Execute(id2);

        selector.SelectPrimaryCalls.Should().ContainSingle().Which.Should().Be(id2);
        vm.Devices.Single(d => d.Id == id2).IsPrimary.Should().BeTrue();
        vm.Devices.Single(d => d.Id == id1).IsPrimary.Should().BeFalse();
    }

    [Fact]
    public void RefreshCommand_re_queries_selector_and_registry()
    {
        var id1 = Guid.NewGuid();
        var idNew = Guid.NewGuid();
        var selector = new FakeSelector();
        selector.AddEntry(MakeEntry(id1));

        var vm = BuildVm(selector);
        vm.Devices.Should().HaveCount(1);

        // Modify selector state and refresh
        selector.AddEntry(MakeEntry(idNew, "New Device"));
        vm.RefreshCommand.Execute(null);

        vm.Devices.Should().HaveCount(2);
        vm.Devices.Should().Contain(d => d.Id == idNew);
    }

    [Fact]
    public void Calibration_status_reflects_registry_state()
    {
        var idCalibrated = Guid.NewGuid();
        var idPending = Guid.NewGuid();

        var selector = new FakeSelector();
        selector.AddEntry(MakeEntry(idCalibrated, "Calibrated Device"));
        selector.AddEntry(MakeEntry(idPending, "Pending Device"));

        var registry = new FakeRegistry();
        registry.SetStatus(idCalibrated, DeviceCalibrationStatus.Calibrated);
        registry.SetStatus(idPending, DeviceCalibrationStatus.NotCalibrated);

        var vm = BuildVm(selector, registry);

        vm.Devices.Single(d => d.Id == idCalibrated).CalibrationStatus
            .Should().Be(DeviceCalibrationStatus.Calibrated);
        vm.Devices.Single(d => d.Id == idPending).CalibrationStatus
            .Should().Be(DeviceCalibrationStatus.NotCalibrated);
    }

    [Fact]
    public void Disconnected_devices_remain_in_list_with_IsConnected_false()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var selector = new FakeSelector();
        selector.AddEntry(MakeEntry(idA, "Device A"));
        selector.AddEntry(MakeEntry(idB, "Device B"));

        var vm = BuildVm(selector);
        vm.Devices.Should().HaveCount(2);

        // Fire topology with only idA connected
        selector.FireTopologyChanged(new[] { MakeEntry(idA) });

        vm.Devices.Should().HaveCount(2, "disconnected row must be preserved");
        var rowB = vm.Devices.Single(d => d.Id == idB);
        rowB.IsConnected.Should().BeFalse();
        rowB.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public void Selecting_primary_for_disconnected_device_is_rejected()
    {
        var idConnected = Guid.NewGuid();
        var idDisconnected = Guid.NewGuid();

        var selector = new FakeSelector();
        selector.AddEntry(MakeEntry(idConnected));
        selector.AddEntry(MakeEntry(idDisconnected, isConnected: false));

        var vm = BuildVm(selector);

        vm.MakePrimaryCommand.CanExecute(idConnected).Should().BeTrue();
        vm.MakePrimaryCommand.CanExecute(idDisconnected).Should().BeFalse();
    }
}
