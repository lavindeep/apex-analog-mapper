using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.Core.Engine;
using ApexMapper.Persistence.Profiles;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Profiles;

public sealed class ProfileSelectorViewModelTests : IDisposable
{
    private readonly string _storeDir = Path.Combine(
        Path.GetTempPath(), "ApexMapper_VM_" + Guid.NewGuid().ToString("N"));

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

    private sealed class FakeResolver
    {
        public string? ResolvedId { get; set; }
        public string? Resolve() => ResolvedId;
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

    private ProfileStore CreateStoreWith(params Profile[] profiles)
    {
        Directory.CreateDirectory(_storeDir);
        var store = new ProfileStore(new ProfileStoreOptions(_storeDir));
        foreach (var p in profiles)
            store.Save(p);
        return store;
    }

    private static ProfileSelectorViewModel BuildVm(
        ProfileStore store,
        FakePinStore pinStore,
        FakeResolver resolver) =>
        new(store, pinStore, () => resolver.Resolve());

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void Constructor_loads_profiles_from_store()
    {
        var store = CreateStoreWith(
            MakeProfile("p1", "Profile One"),
            MakeProfile("p2", "Profile Two"),
            MakeProfile("p3", "Profile Three"));
        var vm = BuildVm(store, new FakePinStore(), new FakeResolver());

        vm.Profiles.Should().HaveCount(3);
        vm.Profiles.Should().Contain(p => p.Id == "p1");
        vm.Profiles.Should().Contain(p => p.Id == "p2");
        vm.Profiles.Should().Contain(p => p.Id == "p3");
    }

    [Fact]
    public void PinCommand_persists_pin_in_store()
    {
        var store = CreateStoreWith(MakeProfile("p1", "One"), MakeProfile("p2", "Two"));
        var pinStore = new FakePinStore();
        var vm = BuildVm(store, pinStore, new FakeResolver());

        vm.PinCommand.Execute("p2");

        pinStore.Get().Should().Be("p2");
    }

    [Fact]
    public void UnpinCommand_clears_pin()
    {
        var store = CreateStoreWith(MakeProfile("p1", "One"), MakeProfile("p2", "Two"));
        var pinStore = new FakePinStore();
        var vm = BuildVm(store, pinStore, new FakeResolver());

        vm.PinCommand.Execute("p2");
        vm.UnpinCommand.Execute(null);

        pinStore.Get().Should().BeNull();
    }

    [Fact]
    public void Manual_pin_overrides_resolver_for_current_id()
    {
        var store = CreateStoreWith(MakeProfile("p2", "Two"), MakeProfile("p3", "Three"));
        var pinStore = new FakePinStore();
        var resolver = new FakeResolver { ResolvedId = "p3" };
        var vm = BuildVm(store, pinStore, resolver);

        vm.PinCommand.Execute("p2");

        vm.PinnedProfileId.Should().Be("p2");
        // CurrentProfileId: pinned wins over resolved
        vm.CurrentProfileId.Should().Be("p2");
    }

    [Fact]
    public void RefreshCommand_reloads_from_store()
    {
        var store = CreateStoreWith(MakeProfile("p1", "One"));
        var vm = BuildVm(store, new FakePinStore(), new FakeResolver());
        vm.Profiles.Should().HaveCount(1);

        store.Save(MakeProfile("p2", "Two"));
        vm.RefreshCommand.Execute(null);

        vm.Profiles.Should().HaveCount(2);
    }

    [Fact]
    public void IsPinned_flag_is_set_on_pinned_row_only()
    {
        var store = CreateStoreWith(MakeProfile("p1", "One"), MakeProfile("p2", "Two"));
        var vm = BuildVm(store, new FakePinStore(), new FakeResolver());

        vm.PinCommand.Execute("p2");

        vm.Profiles.Single(p => p.Id == "p2").IsPinned.Should().BeTrue();
        vm.Profiles.Single(p => p.Id == "p1").IsPinned.Should().BeFalse();
    }

    [Fact]
    public void Profiles_collection_change_raises_PropertyChanged()
    {
        var store = CreateStoreWith(MakeProfile("p1", "One"));
        var vm = BuildVm(store, new FakePinStore(), new FakeResolver());

        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.Save(MakeProfile("p2", "Two"));
        vm.RefreshCommand.Execute(null);

        changed.Should().Contain(nameof(ProfileSelectorViewModel.Profiles));
    }

    [Fact]
    public void CurrentProfileId_falls_back_to_resolved_when_no_pin()
    {
        var store = CreateStoreWith(MakeProfile("p3", "Three"));
        var resolver = new FakeResolver { ResolvedId = "p3" };
        var vm = BuildVm(store, new FakePinStore(), resolver);

        vm.CurrentProfileId.Should().Be("p3");
    }

    [Fact]
    public void CurrentProfileId_is_null_when_no_pin_and_resolver_returns_null()
    {
        var store = CreateStoreWith(MakeProfile("p1", "One"));
        var vm = BuildVm(store, new FakePinStore(), new FakeResolver { ResolvedId = null });

        vm.CurrentProfileId.Should().BeNull();
    }
}
