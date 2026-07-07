using System;
using System.Collections.Generic;
using ApexMapper.App.Services;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApexMapper.App.Tests.Profiles;

public sealed class ProfileActivationServiceTests
{
    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    private sealed class FakeHotReload : IProfileHotReload
    {
        public event EventHandler<ProfilesReloadedEventArgs>? ProfilesReloaded;

        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void RaiseReloaded(IReadOnlyList<Profile> profiles)
            => ProfilesReloaded?.Invoke(this, new ProfilesReloadedEventArgs(profiles));
    }

    private sealed class FakeForegroundWatcher : IForegroundWatcher
    {
        public ApexMapper.Core.ForegroundContext Current { get; set; } = ApexMapper.Core.ForegroundContext.Empty;

        public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void MoveTo(string exePath)
        {
            Current = new ApexMapper.Core.ForegroundContext(exePath, "Window", 99u, null, DateTimeOffset.UtcNow);
            ForegroundChanged?.Invoke(this, new ForegroundChangedEventArgs(Current));
        }
    }

    private sealed class FakePinStore : IProfileManualPinStore
    {
        public string? Pinned { get; set; }
        public Exception? ThrowOnGet { get; set; }

        public string? Get()
        {
            if (ThrowOnGet is not null) throw ThrowOnGet;
            return Pinned;
        }

        public void Set(string? profileId) => Pinned = profileId;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Profile MakeProfile(string id, string? exeName = null) => new(
        id,
        id,
        new DeviceMatcher(0x1038, 0x161C, null, null),
        new GameMatcher(exeName, null, null),
        ActivationPolicy.Default,
        new[]
        {
            new SingleKeyBinding(KeyId.FromScanCode(0x11), BindingTarget.RightTrigger, LinearCurve.Instance, 120f, 0f),
        },
        Array.Empty<AxisPairBinding>(),
        Notes: null);

    private sealed record Harness(
        ProfileActivationService Service,
        FakeHotReload HotReload,
        FakeForegroundWatcher Foreground,
        FakePinStore PinStore,
        List<Profile?> Applied);

    private static Harness Build(
        IReadOnlyList<Profile>? profiles = null,
        Exception? loadFailure = null)
    {
        var hotReload = new FakeHotReload();
        var foreground = new FakeForegroundWatcher();
        var pinStore = new FakePinStore();
        var applied = new List<Profile?>();

        var service = new ProfileActivationService(
            loadProfiles: () => loadFailure is not null
                ? throw loadFailure
                : profiles ?? Array.Empty<Profile>(),
            hotReload,
            foreground,
            pinStore,
            applyProfile: applied.Add,
            NullLogger<ProfileActivationService>.Instance);

        return new Harness(service, hotReload, foreground, pinStore, applied);
    }

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void Start_applies_the_generic_profile_when_nothing_matches()
    {
        var generic = MakeProfile("generic");
        var h = Build(new[] { generic, MakeProfile("forza", "Forza.exe") });

        h.Service.Start();

        h.Applied.Should().ContainSingle().Which.Should().BeSameAs(generic);
        h.Service.CurrentProfileId.Should().Be("generic");
    }

    [Fact]
    public void Start_with_a_failing_load_applies_no_profile_instead_of_throwing()
    {
        var h = Build(loadFailure: new InvalidOperationException("disk broke"));

        var act = () => h.Service.Start();

        act.Should().NotThrow();
        h.Applied.Should().ContainSingle().Which.Should().BeNull("mapping nothing beats mapping something stale");
        h.Service.CurrentProfileId.Should().BeNull();
    }

    [Fact]
    public void Foreground_change_swaps_to_the_matching_profile()
    {
        var generic = MakeProfile("generic");
        var forza = MakeProfile("forza", "Forza.exe");
        var h = Build(new[] { generic, forza });
        h.Service.Start();

        h.Foreground.MoveTo("C:/Games/Forza.exe");

        h.Applied.Should().HaveCount(2);
        h.Applied[^1].Should().BeSameAs(forza);
        h.Service.CurrentProfileId.Should().Be("forza");
    }

    [Fact]
    public void Same_resolution_is_not_reapplied_so_ramps_survive_alt_tab()
    {
        var generic = MakeProfile("generic");
        var h = Build(new[] { generic });
        h.Service.Start();

        h.Foreground.MoveTo("C:/Other/App.exe");
        h.Foreground.MoveTo("C:/Another/Tool.exe");

        h.Applied.Should().ContainSingle("re-applying the same profile would reset ramp state mid-hold");
    }

    [Fact]
    public void Hot_reload_reapplies_even_when_the_id_is_unchanged()
    {
        var v1 = MakeProfile("generic");
        var h = Build(new[] { v1 });
        h.Service.Start();

        var v2 = MakeProfile("generic");
        var reloadedRaised = 0;
        h.Service.ProfilesReloaded += (_, _) => reloadedRaised++;
        h.HotReload.RaiseReloaded(new[] { v2 });

        h.Applied.Should().HaveCount(2, "the same id may carry edited bindings after a reload");
        h.Applied[^1].Should().BeSameAs(v2);
        reloadedRaised.Should().Be(1);
    }

    [Fact]
    public void Pin_wins_over_the_foreground_match_after_reevaluate()
    {
        var generic = MakeProfile("generic");
        var forza = MakeProfile("forza", "Forza.exe");
        var h = Build(new[] { generic, forza });
        h.Service.Start();
        h.Foreground.MoveTo("C:/Games/Forza.exe");

        h.PinStore.Pinned = "generic";
        h.Service.Reevaluate();

        h.Applied[^1].Should().BeSameAs(generic);
        h.Service.CurrentProfileId.Should().Be("generic");
    }

    [Fact]
    public void A_throwing_pin_store_resolves_without_the_pin()
    {
        var generic = MakeProfile("generic");
        var h = Build(new[] { generic });
        h.PinStore.ThrowOnGet = new InvalidOperationException("pin file corrupt");

        h.Service.Start();

        h.Applied.Should().ContainSingle().Which.Should().BeSameAs(generic);
    }

    [Fact]
    public void ActiveProfileChanged_fires_only_on_id_changes()
    {
        var generic = MakeProfile("generic");
        var forza = MakeProfile("forza", "Forza.exe");
        var h = Build(new[] { generic, forza });
        var changes = 0;
        h.Service.ActiveProfileChanged += (_, _) => changes++;

        h.Service.Start();                          // (none) -> generic
        h.Foreground.MoveTo("C:/Games/Forza.exe"); // generic -> forza
        h.Foreground.MoveTo("C:/Games/Forza.exe"); // no change

        changes.Should().Be(2);
    }

    [Fact]
    public void Dispose_stops_reacting_to_sources()
    {
        var generic = MakeProfile("generic");
        var forza = MakeProfile("forza", "Forza.exe");
        var h = Build(new[] { generic, forza });
        h.Service.Start();

        h.Service.Dispose();
        h.Foreground.MoveTo("C:/Games/Forza.exe");

        h.Applied.Should().ContainSingle("a disposed service must not keep steering the engine");
    }
}
