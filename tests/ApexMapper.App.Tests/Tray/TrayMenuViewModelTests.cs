using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels.Tray;
using ApexMapper.Core;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Tray;

public sealed class TrayMenuViewModelTests
{
    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    // FakeTrayService implements ITrayServiceInternal (which extends ITrayService).
    // The VM now takes ITrayServiceInternal directly, so no cast is needed.
    private sealed class FakeTrayService : ITrayServiceInternal
    {
        public bool IsEnabled { get; private set; }
        public bool SetEnabledCalled { get; private set; }

        public event EventHandler? OpenMainWindowRequested;
        public event EventHandler? ExitRequested;

        public void Show() { }
        public void Hide() { }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            SetEnabledCalled = true;
        }

        public void SetTooltip(string text) { }
        public void ShowBalloon(string title, string message) { }

        // ITrayServiceInternal — called by TrayMenuViewModel commands
        public void RequestOpenMainWindow() => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
        public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

        public void Dispose() { }
    }

    private sealed class FakeTrayProfileSource : ITrayProfileSource
    {
        private readonly List<TrayProfileEntry> _profiles;
        private string _currentProfileId;

        public FakeTrayProfileSource(IEnumerable<TrayProfileEntry> profiles, string currentId)
        {
            _profiles = [..profiles];
            _currentProfileId = currentId;
        }

        public string CurrentProfileId => _currentProfileId;

        public IReadOnlyList<TrayProfileEntry> ListProfiles() => _profiles.AsReadOnly();

        public void Switch(string profileId) => _currentProfileId = profileId;

        public void ReplaceProfiles(IEnumerable<TrayProfileEntry> profiles)
        {
            _profiles.Clear();
            _profiles.AddRange(profiles);
        }

        public event EventHandler? ProfilesChanged;

        public void RaiseProfilesChanged() => ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSupervisorChannel : ISupervisorChannel
    {
        public bool PanicSubmitted { get; private set; }

        public bool IsConnected => false;

        public event EventHandler<SupervisorStatusEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SubmitPanicAsync(CancellationToken ct)
        {
            PanicSubmitted = true;
            return Task.CompletedTask;
        }

        public Task SubmitControlAsync(ApexMapper.Core.Pipeline.VirtualPadState state, CancellationToken ct)
            => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class FakeHotkeyService : IHotkeyService
    {
        public void Register(string id, HotkeyGesture gesture, Action callback) { }
        public void Unregister(string id) { }
        public bool IsRegistered(string id) => false;
        public void Dispose() { }
    }

    private sealed class FakeForegroundWatcher : IForegroundWatcher
    {
        public ForegroundContext Current => ForegroundContext.Empty;
        public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged
        {
            add { }
            remove { }
        }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class FakePanicPolicyStore : IPanicPolicyStore
    {
        public bool IsAutoEnableDisabled(string executablePath) => false;
        public void DisableAutoEnable(string executablePath) { }
        public void EnableAutoEnable(string executablePath) { }
        public IReadOnlyCollection<string> ListDisabled() => Array.Empty<string>();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static readonly TrayProfileEntry Profile1 = new("p1", "Profile One");
    private static readonly TrayProfileEntry Profile2 = new("p2", "Profile Two");

    private static (TrayMenuViewModel vm, FakeTrayService tray, FakeTrayProfileSource source, FakeSupervisorChannel channel, PanicCoordinator coordinator)
        Build(string currentId = "p1")
    {
        var tray = new FakeTrayService();
        var source = new FakeTrayProfileSource([Profile1, Profile2], currentId);
        var channel = new FakeSupervisorChannel();
        var coordinator = new PanicCoordinator(
            new FakeHotkeyService(),
            channel,
            new FakeForegroundWatcher(),
            new FakePanicPolicyStore());
        var vm = new TrayMenuViewModel(tray, source, coordinator);
        return (vm, tray, source, channel, coordinator);
    }

    // ---------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------

    [Fact]
    public void NewVm_IsEnabled_is_false()
    {
        var (vm, _, _, _, _) = Build();
        vm.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void NewVm_Profiles_reflects_source()
    {
        var (vm, _, _, _, _) = Build();
        vm.Profiles.Should().HaveCount(2)
            .And.Contain(p => p.ProfileId == "p1")
            .And.Contain(p => p.ProfileId == "p2");
    }

    [Fact]
    public void NewVm_CurrentProfileName_matches_source_current_id()
    {
        var (vm, _, _, _, _) = Build("p1");
        vm.CurrentProfileName.Should().Be("Profile One");
    }

    // ---------------------------------------------------------------------------
    // ToggleEnabledCommand
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToggleEnabledCommand_flips_IsEnabled_to_true()
    {
        var (vm, _, _, _, _) = Build();
        vm.ToggleEnabledCommand.Execute(null);
        vm.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void ToggleEnabledCommand_calls_tray_SetEnabled_with_new_value()
    {
        var (vm, tray, _, _, _) = Build();
        vm.ToggleEnabledCommand.Execute(null);
        tray.SetEnabledCalled.Should().BeTrue();
        tray.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void ToggleEnabledCommand_flips_back_to_false_on_second_call()
    {
        var (vm, tray, _, _, _) = Build();
        vm.ToggleEnabledCommand.Execute(null);
        vm.ToggleEnabledCommand.Execute(null);
        vm.IsEnabled.Should().BeFalse();
        tray.IsEnabled.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------
    // SwitchProfileCommand
    // ---------------------------------------------------------------------------

    [Fact]
    public void SwitchProfileCommand_calls_source_Switch()
    {
        var (vm, _, source, _, _) = Build("p1");
        vm.SwitchProfileCommand.Execute("p2");
        source.CurrentProfileId.Should().Be("p2");
    }

    [Fact]
    public void SwitchProfileCommand_refreshes_CurrentProfileName()
    {
        var (vm, _, _, _, _) = Build("p1");
        vm.SwitchProfileCommand.Execute("p2");
        vm.CurrentProfileName.Should().Be("Profile Two");
    }

    // ---------------------------------------------------------------------------
    // PanicCommand
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PanicCommand_routes_through_coordinator_and_calls_supervisor_channel()
    {
        var (vm, _, _, channel, coordinator) = Build();

        // Use PanicCompleted event to await the fire-and-forget task
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.PanicCompleted += (_, _) => tcs.TrySetResult(true);

        vm.PanicCommand.Execute(null);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        channel.PanicSubmitted.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------
    // ExitCommand
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExitCommand_fires_ExitRequested_event_on_tray_service()
    {
        var (vm, tray, _, _, _) = Build();
        bool raised = false;
        tray.ExitRequested += (_, _) => raised = true;
        vm.ExitCommand.Execute(null);
        raised.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------
    // OpenMainWindowCommand
    // ---------------------------------------------------------------------------

    [Fact]
    public void OpenMainWindowCommand_fires_OpenMainWindowRequested_event_on_tray_service()
    {
        var (vm, tray, _, _, _) = Build();
        bool raised = false;
        tray.OpenMainWindowRequested += (_, _) => raised = true;
        vm.OpenMainWindowCommand.Execute(null);
        raised.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------
    // ProfilesChanged event propagation
    // ---------------------------------------------------------------------------

    [Fact]
    public void ProfilesChanged_event_raises_PropertyChanged_for_Profiles()
    {
        var (vm, _, source, _, _) = Build();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        source.RaiseProfilesChanged();

        changed.Should().Contain(nameof(TrayMenuViewModel.Profiles));
    }

    [Fact]
    public void ProfilesChanged_event_raises_PropertyChanged_for_CurrentProfileName_when_renamed()
    {
        var (vm, _, source, _, _) = Build();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Rename the current profile so the resolved display name actually changes;
        // an unchanged name must not notify (standard change-only semantics).
        source.ReplaceProfiles([new TrayProfileEntry("p1", "Profile One Renamed"), Profile2]);
        source.RaiseProfilesChanged();

        changed.Should().Contain(nameof(TrayMenuViewModel.CurrentProfileName));
        vm.CurrentProfileName.Should().Be("Profile One Renamed");
    }
}
