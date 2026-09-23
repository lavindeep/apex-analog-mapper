using System.Windows;
using ApexMapper.App.ViewModels;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Session;
using Xunit;
using CoreResponse = ApexMapper.Core.Response.Response;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class ProfileViewModelTests : IDisposable
{
    private static readonly ScanCode K = new(0x25);
    private static readonly ScanCode J = new(0x24);
    private static readonly ScanCode LeftCtrl = new(0x1D);
    private static readonly ScanCode Escape = new(0x01);

    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private ProfileViewModel Create() => new(_h.Services, _h.Workspace, remembered: null);

    /// <summary>A press stamped just late enough for a capture that began at stamp zero, the harness's clock.</summary>
    private static RawKeyEvent Down(ScanCode key, nint device = 1) => new(key, true, device, ProfileViewModel.CaptureArmTicks);

    private static RawKeyEvent Up(ScanCode key) => new(key, false, 1, ProfileViewModel.CaptureArmTicks);

    private BindingRowViewModel RowOf(ProfileViewModel profile, ScanCode key) => profile.Rows.Single(r => !r.IsAxis && r.Key == key);

    [Fact]
    public void Capture_refuses_a_reserved_key_and_takes_the_next_physical_key_down()
    {
        var profile = Create();
        var rows = profile.Rows.Count;
        profile.AddKey.Execute(null);

        profile.OnKey(Down(K, device: 0));
        profile.OnKey(Up(K));
        profile.OnKey(Down(LeftCtrl));

        Assert.True(profile.IsCapturing);
        Assert.StartsWith("Ctrl, Alt, Windows and F12 cannot be mapped.", profile.Prompt);

        profile.OnKey(Down(K));

        Assert.False(profile.IsCapturing);
        Assert.Equal(rows + 1, profile.Rows.Count);
        Assert.Equal(K, profile.SelectedRow!.Key);
        Assert.True(profile.IsDirty);
    }

    [Fact]
    public void An_axis_takes_two_keys_and_Esc_cancels()
    {
        var profile = Create();
        var rows = profile.Rows.Count;

        profile.AddAxis.Execute(null);
        profile.OnKey(Down(J));
        profile.OnKey(Down(Escape));

        Assert.False(profile.IsCapturing);
        Assert.Equal(rows, profile.Rows.Count);
        profile.OnKey(Up(J));

        profile.AddAxis.Execute(null);
        profile.OnKey(Down(J));
        profile.OnKey(Down(K));

        var axis = profile.SelectedRow!;
        Assert.True(axis.IsAxis);
        Assert.Equal(J, axis.NegativeKey);
        Assert.Equal(K, axis.Key);
    }

    [Fact]
    public void Capture_skips_the_press_that_started_it_and_auto_repeats()
    {
        var profile = Create();
        var enter = new ScanCode(0x1C);

        // Enter clicked the button: its press lands around the moment the capture began, then repeats.
        profile.AddKey.Execute(null);
        profile.OnKey(new RawKeyEvent(enter, true, 1, ProfileViewModel.CaptureArmTicks - 1));
        profile.OnKey(Down(enter));
        Assert.True(profile.IsCapturing);
        profile.OnKey(Up(enter));
        profile.OnKey(Down(K));
        Assert.Equal(K, profile.SelectedRow!.Key);

        // The first key of an axis still held past the repeat delay is not the second.
        profile.AddAxis.Execute(null);
        profile.OnKey(Down(J));
        profile.OnKey(Down(J));
        Assert.True(profile.IsCapturing);
        profile.OnKey(Down(new ScanCode(0x26)));
        Assert.Equal("0x24 and 0x26", profile.SelectedRow!.KeysText);
    }

    [Fact]
    public void Capture_refuses_a_key_another_binding_uses_and_keys_the_hook_reports_differently()
    {
        var profile = Create();
        profile.AddKey.Execute(null);

        profile.OnKey(Down(DefaultProfiles.Key.W));
        Assert.Equal("W is already bound to Right trigger. Press another key, or Esc to cancel.", profile.Prompt);
        profile.OnKey(Down(new ScanCode(0xE11D)));
        Assert.StartsWith("0xE11D cannot be mapped", profile.Prompt);
        profile.OnKey(Down(new ScanCode(0x45)));
        Assert.StartsWith("0x45 cannot be mapped", profile.Prompt);
        Assert.True(profile.IsCapturing);

        // A row may keep its own key; an axis may not take its other direction's.
        profile.CancelCapture.Execute(null);
        profile.SelectedRow = profile.Rows.First(r => r.IsAxis);
        profile.CaptureNegativeKey.Execute(null);
        profile.OnKey(Down(profile.SelectedRow.Key));
        Assert.Equal("The two directions need different keys. Press another key, or Esc to cancel.", profile.Prompt);
        profile.OnKey(Down(profile.SelectedRow.NegativeKey));
        Assert.False(profile.IsCapturing);
        Assert.False(profile.IsDirty);
    }

    [Fact]
    public void Discarding_during_a_capture_ends_it()
    {
        var profile = Create();
        profile.Name = "Drift";
        profile.AddKey.Execute(null);

        profile.Discard.Execute(null);

        Assert.False(profile.IsCapturing);
        Assert.Null(profile.Prompt);
        Assert.True(profile.CanEdit);
        profile.OnKey(Down(K));
        Assert.False(profile.IsDirty);
    }

    [Fact]
    public async Task The_editor_holds_still_while_a_save_waits_for_the_session_to_stop()
    {
        var profile = Create();
        _h.Workspace.RunningProfileText = ProfileJson.Serialize(_h.Workspace.ActiveProfile!);
        _h.Workspace.Session = SessionState.Running;
        profile.Name = "Forza edited";
        Assert.Equal("Saving stops mapping. Press Start again to use the changes.", profile.SaveNote);
        _h.Session.StopGate = new TaskCompletionSource();

        var saving = profile.SaveAsync();

        Assert.False(profile.CanEdit);
        Assert.False(profile.Save.CanExecute(null));
        Assert.False(profile.Reset.CanExecute(null));
        _h.Session.StopGate.SetResult();
        await saving;
        Assert.True(profile.CanEdit);
        Assert.Equal("Forza edited", profile.Name);
    }

    [Fact]
    public async Task Edits_the_profile_check_would_refuse_are_named_in_the_card_s_words()
    {
        var profile = Create();
        var w = RowOf(profile, DefaultProfiles.Key.W);

        w.Deadzone = 0.5;
        w.Saturation = 0.4;
        await profile.SaveAsync();
        Assert.Equal("W: the dead zone must be less than Full output at.", profile.Message);

        w.Saturation = 1;
        w.Target = RowOf(profile, DefaultProfiles.Key.S).Target;
        await profile.SaveAsync();
        Assert.Equal("Left trigger is used by more than one binding. Choose another for one of them.", profile.Message);
        Assert.True(profile.IsDirty);
    }

    [Fact]
    public void The_preview_follows_the_key_in_counts_and_depth()
    {
        _h.ChooseTkl();
        var profile = Create();
        Assert.False(_h.Sensor.Wanted);
        profile.IsOpen = true;
        profile.SelectedRow = RowOf(profile, DefaultProfiles.Key.W);
        Assert.True(_h.Sensor.Wanted);

        profile.Tick();
        Assert.Null(profile.Marker);
        Assert.Equal("Waiting for the keyboard's readings.", profile.PreviewText);

        _h.Sensor.Reading = AppHarness.Holding(16, 2375);
        profile.Tick();
        Assert.Null(profile.Marker);
        Assert.Equal("W: 2375 counts. Calibrate W to see its depth here.", profile.PreviewText);

        _h.CalibrateForza();
        _h.ChooseTkl();
        profile.Tick();
        var depth = Normalizer.Depth(_h.Workspace.Calibration!.Keys[DefaultProfiles.Key.W], 2375);
        var output = profile.SelectedRow.CurrentResponse.Map(depth);
        Assert.Equal(depth * ProfileViewModel.PreviewWidth, profile.Marker!.Value.X, 6);
        Assert.Equal((1 - output) * ProfileViewModel.PreviewHeight, profile.Marker.Value.Y, 6);
        Assert.StartsWith("W: 2375 counts, ", profile.PreviewText);
        Assert.Equal(51, profile.Curve.Count);
        Assert.Equal(new Point(0, ProfileViewModel.PreviewHeight), profile.Curve[0]);
        Assert.Equal(new Point(ProfileViewModel.PreviewWidth, 0), profile.Curve[^1]);

        _h.Workspace.Session = SessionState.Starting;
        Assert.False(_h.Sensor.Wanted);
        Assert.Null(profile.Marker);

        _h.Workspace.Session = SessionState.Idle;
        Assert.True(_h.Sensor.Wanted);
        profile.IsOpen = false;
        Assert.False(_h.Sensor.Wanted);
    }

    [Fact]
    public void An_edit_marks_the_profile_unsaved_and_a_preset_sets_the_whole_response()
    {
        var profile = Create();
        var w = RowOf(profile, DefaultProfiles.Key.W);

        w.Deadzone = 0.1;
        Assert.True(profile.IsDirty);
        Assert.True(profile.Save.CanExecute(null));
        Assert.False(profile.CanSwitch);
        Assert.Equal(ResponsePreset.Custom, w.Preset);

        w.Preset = ResponsePreset.Aggressive;
        Assert.Equal(CoreResponse.Aggressive, w.CurrentResponse);
        Assert.Equal(ResponsePreset.Aggressive, w.Preset);

        // What a screen reader says for the row in the list.
        Assert.Equal("W, Right trigger", w.ToString());
    }

    [Fact]
    public void Changing_one_key_of_an_axis_leaves_the_other()
    {
        var profile = Create();
        var axis = profile.Rows.First(r => r.IsAxis);
        profile.SelectedRow = axis;
        var right = axis.Key;

        profile.CaptureNegativeKey.Execute(null);
        profile.OnKey(Down(J));

        Assert.Equal(J, axis.NegativeKey);
        Assert.Equal(right, axis.Key);
    }

    [Fact]
    public async Task Resetting_the_running_profile_stops_the_session()
    {
        var profile = Create();
        RowOf(profile, DefaultProfiles.Key.W).Deadzone = 0.1;
        await profile.SaveAsync();
        _h.Workspace.RunningProfileText = ProfileJson.Serialize(_h.Workspace.ActiveProfile!);
        _h.Workspace.Session = SessionState.Running;

        await profile.ResetAsync();

        Assert.Equal([EndReason.ProfileEdited], _h.Session.Stops);
        Assert.Equal(DefaultProfiles.Forza().Keys[0].Response, _h.Workspace.ActiveProfile!.Keys[0].Response);
    }

    [Fact]
    public void The_profile_cannot_change_while_a_session_runs()
    {
        var profile = Create();
        profile.New.Execute(null);
        var forza = profile.Profiles.Single(p => p.Id == DefaultProfiles.ForzaId);
        _h.Workspace.Session = SessionState.Running;

        profile.Selected = forza;

        Assert.Equal("profile-2", profile.Selected!.Id);
        Assert.False(profile.CanSwitch);
        Assert.False(profile.New.CanExecute(null));
    }

    [Fact]
    public async Task Saving_the_running_profile_stops_the_session_only_when_the_saved_text_changed()
    {
        var profile = Create();
        _h.Workspace.RunningProfileText = ProfileJson.Serialize(_h.Workspace.ActiveProfile!);
        _h.Workspace.Session = SessionState.Running;

        await profile.SaveAsync();
        Assert.Empty(_h.Session.Stops);

        RowOf(profile, DefaultProfiles.Key.W).Deadzone = 0.1;
        await profile.SaveAsync();

        Assert.Equal([EndReason.ProfileEdited], _h.Session.Stops);
        Assert.Equal(0.1f, _h.Workspace.ActiveProfile!.Keys[0].Response.Deadzone, 6);
        Assert.False(profile.IsDirty);
    }

    [Fact]
    public async Task Deleting_the_running_profile_stops_the_session()
    {
        var profile = Create();
        profile.New.Execute(null);
        Assert.Equal("profile-2", profile.Selected!.Id);
        Assert.Equal("profile-2", _h.SavedSettings.ActiveProfile);
        _h.Workspace.RunningProfileText = ProfileJson.Serialize(_h.Workspace.ActiveProfile!);
        _h.Workspace.Session = SessionState.Running;

        _h.Dialogs.Answer = false;
        await profile.DeleteAsync();
        Assert.Empty(_h.Session.Stops);

        _h.Dialogs.Answer = true;
        await profile.DeleteAsync();

        Assert.Equal([EndReason.ProfileEdited], _h.Session.Stops);
        Assert.Equal(DefaultProfiles.ForzaId, profile.Selected!.Id);
        Assert.DoesNotContain(profile.Profiles, p => p.Id == "profile-2");
    }

    [Fact]
    public void Switching_profiles_waits_for_unsaved_edits()
    {
        var profile = Create();
        profile.New.Execute(null);
        var forza = profile.Profiles.Single(p => p.Id == DefaultProfiles.ForzaId);

        profile.Name = "Drift";
        profile.Selected = forza;

        Assert.Equal("profile-2", profile.Selected!.Id);
        Assert.False(profile.New.CanExecute(null));

        profile.Discard.Execute(null);
        profile.Selected = forza;

        Assert.Equal(DefaultProfiles.ForzaId, profile.Selected!.Id);
        Assert.Equal(DefaultProfiles.ForzaId, _h.SavedSettings.ActiveProfile);
    }
}
