using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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
            for (var i = 0; i < _entries.Count; i++)
                _entries[i] = _entries[i] with { IsPrimary = _entries[i].Id == id };
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

    private static DevicePickerViewModel BuildVm(FakeSelector selector) => new(selector);

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
    public void Sources_in_one_container_show_one_keyboard_and_preserve_the_selected_source()
    {
        var first = MakeEntry(Guid.NewGuid(), "Apex Pro TKL") with
            { PhysicalDeviceId = "keyboard-a", DevicePath = "interface-00" };
        var second = MakeEntry(Guid.NewGuid(), "Apex Pro TKL", isPrimary: true) with
            { PhysicalDeviceId = "keyboard-a", DevicePath = "interface-02" };
        var selector = new FakeSelector();
        selector.AddEntry(first);
        selector.AddEntry(second);

        var vm = BuildVm(selector);

        var keyboard = vm.KeyboardGroups.Should().ContainSingle().Which;
        keyboard.Sources.Select(source => source.Id).Should().Equal(first.Id, second.Id);
        vm.SelectedKeyboard.Should().Be(keyboard);
        vm.SelectedSource!.Id.Should().Be(second.Id);
        selector.SelectPrimaryCalls.Should().BeEmpty();

        vm.SelectKeyboardCommand.Execute(keyboard);

        selector.SelectPrimaryCalls.Should().ContainSingle().Which.Should().Be(second.Id);
        vm.SelectedSource!.Id.Should().Be(second.Id);
    }

    [Fact]
    public void Same_product_with_distinct_or_unknown_containers_stays_separate()
    {
        var selector = new FakeSelector();
        foreach (var container in new string?[] { "keyboard-a", "keyboard-b", null, null })
            selector.AddEntry(MakeEntry(Guid.NewGuid(), "Apex Pro TKL") with
                { PhysicalDeviceId = container });

        var vm = BuildVm(selector);

        vm.KeyboardGroups.Should().HaveCount(4);
        vm.KeyboardGroups.Should().OnlyContain(group => group.Sources.Count == 1);
    }

    [Fact]
    public void Detach_retains_keyboard_and_reconnect_restores_exact_source_without_selecting()
    {
        var first = MakeEntry(Guid.NewGuid(), "Apex Pro TKL") with
            { PhysicalDeviceId = "keyboard-a", DevicePath = "interface-00" };
        var second = MakeEntry(Guid.NewGuid(), "Apex Pro TKL", isPrimary: true) with
            { PhysicalDeviceId = "keyboard-a", DevicePath = "interface-02" };
        var other = MakeEntry(Guid.NewGuid(), "Other keyboard", isConnected: false) with
            { PhysicalDeviceId = "keyboard-b" };
        var selector = new FakeSelector();
        selector.AddEntry(first);
        selector.AddEntry(second);
        selector.AddEntry(other);
        var vm = BuildVm(selector);

        selector.ReplaceEntries([]);
        vm.RefreshCommand.Execute(null);

        vm.SelectedKeyboard!.Id.Should().Be("keyboard-a");
        vm.SelectedKeyboard.IsConnected.Should().BeFalse();
        vm.SelectedSource.Should().BeNull();
        vm.SelectedKeyboard = vm.KeyboardGroups.Single(group => group.Id == "keyboard-b");
        vm.SelectedKeyboard.Id.Should().Be("keyboard-a");

        selector.ReplaceEntries([first, second]);
        vm.RefreshCommand.Execute(null);

        vm.SelectedKeyboard!.Id.Should().Be("keyboard-a");
        vm.SelectedKeyboard.IsConnected.Should().BeTrue();
        vm.SelectedSource!.Id.Should().Be(second.Id);
        selector.SelectPrimaryCalls.Should().BeEmpty();
    }

    [Fact]
    public void Selecting_a_keyboard_then_an_advanced_source_sends_the_exact_source_ids()
    {
        var first = MakeEntry(Guid.NewGuid(), "Apex Pro TKL") with
            { PhysicalDeviceId = "keyboard-a", DevicePath = "interface-00" };
        var second = MakeEntry(Guid.NewGuid(), "Apex Pro TKL") with
            { PhysicalDeviceId = "keyboard-a", DevicePath = "interface-02" };
        var selector = new FakeSelector();
        selector.AddEntry(second);
        selector.AddEntry(first);
        var vm = BuildVm(selector);

        vm.SelectedKeyboard = vm.KeyboardGroups.Single();
        vm.SelectedSource!.Id.Should().Be(first.Id);
        vm.SelectedSource = vm.SelectedKeyboard!.Sources.Single(source => source.Id == second.Id);

        selector.SelectPrimaryCalls.Should().Equal(first.Id, second.Id);
        vm.SelectedSource!.Id.Should().Be(second.Id);
        vm.SelectedKeyboard!.SelectedSource!.Id.Should().Be(second.Id);
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

    // Records posts and runs them inline so the merge still applies synchronously.
    private sealed class RecordingSyncContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            d(state);
        }
    }

    [Fact]
    public void TopologyChanged_from_another_thread_is_marshalled_through_captured_context()
    {
        var id1 = Guid.NewGuid();
        var idNew = Guid.NewGuid();
        var selector = new FakeSelector();
        selector.AddEntry(MakeEntry(id1));

        // Construct the VM under a captured recording context (stands in for the UI thread).
        var ctx = new RecordingSyncContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        DevicePickerViewModel vm;
        try
        {
            vm = BuildVm(selector);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        // Fire the event from a thread where the captured context is NOT current.
        var worker = new Thread(() =>
            selector.FireTopologyChanged(new[] { MakeEntry(id1), MakeEntry(idNew, "New Device") }));
        worker.Start();
        worker.Join();

        ctx.PostCount.Should().BeGreaterThan(0, "the merge must be posted onto the captured context");
        vm.Devices.Should().Contain(d => d.Id == idNew);
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
