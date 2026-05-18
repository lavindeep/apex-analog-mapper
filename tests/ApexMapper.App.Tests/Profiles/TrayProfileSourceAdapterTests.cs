using System;
using System.Collections.Generic;
using System.IO;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.Core.Engine;
using ApexMapper.Persistence.Profiles;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Profiles;

public sealed class TrayProfileSourceAdapterTests : IDisposable
{
    private readonly string _storeDir = Path.Combine(
        Path.GetTempPath(), "ApexMapper_Tray_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }

    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    private sealed class FakePinStore : IProfileManualPinStore
    {
        private string? _pin;
        public string? Get() => _pin;
        public void Set(string? profileId) => _pin = profileId;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Profile MakeProfile(string id, string name) =>
        new(id, name,
            new DeviceMatcher(0, 0, null, null),
            new GameMatcher(null, null, null),
            ActivationPolicy.Default,
            Array.Empty<ApexMapper.Core.Pipeline.SingleKeyBinding>(),
            Array.Empty<ApexMapper.Core.Pipeline.AxisPairBinding>(),
            null);

    private (ProfileSelectorViewModel vm, TrayProfileSourceAdapter adapter) Build(
        string? resolvedId = null,
        params Profile[] profiles)
    {
        Directory.CreateDirectory(_storeDir);
        var store = new ProfileStore(new ProfileStoreOptions(_storeDir));
        foreach (var p in profiles)
            store.Save(p);

        var pinStore = new FakePinStore();
        var vm = new ProfileSelectorViewModel(store, pinStore, () => resolvedId);
        var adapter = new TrayProfileSourceAdapter(vm);
        return (vm, adapter);
    }

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void ListProfiles_returns_entries_matching_vm_profiles()
    {
        var (_, adapter) = Build(null,
            MakeProfile("p1", "Profile One"),
            MakeProfile("p2", "Profile Two"));

        var list = adapter.ListProfiles();

        list.Should().HaveCount(2);
        list.Should().Contain(e => e.ProfileId == "p1" && e.DisplayName == "Profile One");
        list.Should().Contain(e => e.ProfileId == "p2" && e.DisplayName == "Profile Two");
    }

    [Fact]
    public void CurrentProfileId_returns_pinned_id_when_pinned()
    {
        var (vm, adapter) = Build("p3",
            MakeProfile("p2", "Two"),
            MakeProfile("p3", "Three"));

        vm.PinCommand.Execute("p2");

        adapter.CurrentProfileId.Should().Be("p2");
    }

    [Fact]
    public void CurrentProfileId_returns_resolved_id_when_no_pin()
    {
        var (_, adapter) = Build("p3", MakeProfile("p3", "Three"));

        adapter.CurrentProfileId.Should().Be("p3");
    }

    [Fact]
    public void Switch_calls_pin_command_on_vm()
    {
        var (vm, adapter) = Build(null,
            MakeProfile("p1", "One"),
            MakeProfile("p2", "Two"));

        adapter.Switch("p2");

        vm.PinnedProfileId.Should().Be("p2");
    }

    [Fact]
    public void ProfilesChanged_fires_when_vm_profiles_collection_changes()
    {
        Directory.CreateDirectory(_storeDir);
        var store = new ProfileStore(new ProfileStoreOptions(_storeDir));
        store.Save(MakeProfile("p1", "One"));
        var pinStore = new FakePinStore();
        var vm = new ProfileSelectorViewModel(store, pinStore, () => null);
        var adapter = new TrayProfileSourceAdapter(vm);

        bool raised = false;
        adapter.ProfilesChanged += (_, _) => raised = true;

        store.Save(MakeProfile("p2", "Two"));
        vm.RefreshCommand.Execute(null);

        raised.Should().BeTrue();
    }

    [Fact]
    public void ProfilesChanged_fires_when_pin_changes()
    {
        var (vm, adapter) = Build(null,
            MakeProfile("p1", "One"),
            MakeProfile("p2", "Two"));

        bool raised = false;
        adapter.ProfilesChanged += (_, _) => raised = true;

        vm.PinCommand.Execute("p2");

        raised.Should().BeTrue();
    }

    [Fact]
    public void Dispose_unsubscribes_from_vm_events()
    {
        var (vm, adapter) = Build(null, MakeProfile("p1", "One"));

        bool raised = false;
        adapter.ProfilesChanged += (_, _) => raised = true;

        adapter.Dispose();

        // After dispose, mutating the collection should NOT fire ProfilesChanged.
        Directory.CreateDirectory(_storeDir);
        var store = new ProfileStore(new ProfileStoreOptions(_storeDir));
        store.Save(MakeProfile("p1", "One"));
        store.Save(MakeProfile("p2", "Two"));

        // Trigger a Profiles property change on the VM by refreshing
        vm.RefreshCommand.Execute(null);

        raised.Should().BeFalse();
    }

    [Fact]
    public void Profiles_property_change_does_not_accumulate_handlers()
    {
        Directory.CreateDirectory(_storeDir);
        var store = new ProfileStore(new ProfileStoreOptions(_storeDir));
        store.Save(MakeProfile("p1", "One"));

        var pinStore = new FakePinStore();
        var vm = new ProfileSelectorViewModel(store, pinStore, () => null);
        var adapter = new TrayProfileSourceAdapter(vm);

        int eventCount = 0;
        adapter.ProfilesChanged += (_, _) => eventCount++;

        // Replace the Profiles collection twice via refresh
        store.Save(MakeProfile("p2", "Two"));
        vm.RefreshCommand.Execute(null);   // first Profiles replacement

        store.Save(MakeProfile("p3", "Three"));
        vm.RefreshCommand.Execute(null);   // second Profiles replacement

        // Reset counter — now mutate only the latest collection
        eventCount = 0;
        store.Save(MakeProfile("p4", "Four"));
        vm.RefreshCommand.Execute(null);

        // Exactly one event should fire, not two (no handler accumulation)
        eventCount.Should().Be(1);
    }
}
