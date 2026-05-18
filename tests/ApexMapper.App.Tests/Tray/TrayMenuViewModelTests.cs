using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels.Tray;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Tray;

public sealed class TrayMenuViewModelTests
{
    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    private sealed class FakeTrayService : ITrayService, ITrayServiceInternal
    {
        public bool IsEnabled { get; private set; }
        public bool SetEnabledCalled { get; private set; }

#pragma warning disable CS0067 // event never used — required by ITrayService interface
        public event EventHandler? OpenMainWindowRequested;
#pragma warning restore CS0067
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

        // ITrayServiceInternal — called by TrayMenuViewModel.ExitCommand
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

        public event EventHandler? ProfilesChanged;

        public void RaiseProfilesChanged() => ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSupervisorChannel : ISupervisorChannel
    {
        public bool PanicSubmitted { get; private set; }

        public bool IsConnected => false;

#pragma warning disable CS0067 // event never used — required by ISupervisorChannel interface
        public event EventHandler<SupervisorStatusEventArgs>? StatusChanged;
#pragma warning restore CS0067

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

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static readonly TrayProfileEntry Profile1 = new("p1", "Profile One", true);
    private static readonly TrayProfileEntry Profile2 = new("p2", "Profile Two", false);

    private static (TrayMenuViewModel vm, FakeTrayService tray, FakeTrayProfileSource source, FakeSupervisorChannel channel)
        Build(string currentId = "p1")
    {
        var tray = new FakeTrayService();
        var source = new FakeTrayProfileSource([Profile1, Profile2], currentId);
        var channel = new FakeSupervisorChannel();
        var vm = new TrayMenuViewModel(tray, source, channel);
        return (vm, tray, source, channel);
    }

    // ---------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------

    [Fact]
    public void NewVm_IsEnabled_is_false()
    {
        var (vm, _, _, _) = Build();
        vm.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void NewVm_Profiles_reflects_source()
    {
        var (vm, _, _, _) = Build();
        vm.Profiles.Should().HaveCount(2)
            .And.Contain(p => p.ProfileId == "p1")
            .And.Contain(p => p.ProfileId == "p2");
    }

    [Fact]
    public void NewVm_CurrentProfileName_matches_source_current_id()
    {
        var (vm, _, _, _) = Build("p1");
        vm.CurrentProfileName.Should().Be("Profile One");
    }

    // ---------------------------------------------------------------------------
    // ToggleEnabledCommand
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToggleEnabledCommand_flips_IsEnabled_to_true()
    {
        var (vm, _, _, _) = Build();
        vm.ToggleEnabledCommand.Execute(null);
        vm.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void ToggleEnabledCommand_calls_tray_SetEnabled_with_new_value()
    {
        var (vm, tray, _, _) = Build();
        vm.ToggleEnabledCommand.Execute(null);
        tray.SetEnabledCalled.Should().BeTrue();
        tray.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void ToggleEnabledCommand_flips_back_to_false_on_second_call()
    {
        var (vm, tray, _, _) = Build();
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
        var (vm, _, source, _) = Build("p1");
        vm.SwitchProfileCommand.Execute("p2");
        source.CurrentProfileId.Should().Be("p2");
    }

    [Fact]
    public void SwitchProfileCommand_refreshes_CurrentProfileName()
    {
        var (vm, _, _, _) = Build("p1");
        vm.SwitchProfileCommand.Execute("p2");
        vm.CurrentProfileName.Should().Be("Profile Two");
    }

    // ---------------------------------------------------------------------------
    // PanicCommand
    // ---------------------------------------------------------------------------

    [Fact]
    public void PanicCommand_submits_panic_to_supervisor_channel()
    {
        var (vm, _, _, channel) = Build();
        vm.PanicCommand.Execute(null);
        channel.PanicSubmitted.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------
    // ExitCommand
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExitCommand_fires_ExitRequested_event_on_tray_service()
    {
        var (vm, tray, _, _) = Build();
        bool raised = false;
        tray.ExitRequested += (_, _) => raised = true;
        vm.ExitCommand.Execute(null);
        raised.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------
    // ProfilesChanged event propagation
    // ---------------------------------------------------------------------------

    [Fact]
    public void ProfilesChanged_event_raises_PropertyChanged_for_Profiles()
    {
        var (vm, _, source, _) = Build();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        source.RaiseProfilesChanged();

        changed.Should().Contain(nameof(TrayMenuViewModel.Profiles));
    }

    [Fact]
    public void ProfilesChanged_event_raises_PropertyChanged_for_CurrentProfileName()
    {
        var (vm, _, source, _) = Build();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        source.RaiseProfilesChanged();

        changed.Should().Contain(nameof(TrayMenuViewModel.CurrentProfileName));
    }
}
