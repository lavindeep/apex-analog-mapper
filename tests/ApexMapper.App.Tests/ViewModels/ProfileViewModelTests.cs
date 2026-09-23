using ApexMapper.App.ViewModels;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Profiles;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Session;
using Xunit;

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

    private static RawKeyEvent Down(ScanCode key, nint device = 1) => new(key, true, device, 0);

    [Fact]
    public void Capture_refuses_a_reserved_key_and_takes_the_next_physical_key_down()
    {
        var profile = Create();
        var rows = profile.Rows.Count;
        profile.AddKey.Execute(null);

        profile.OnKey(Down(K, device: 0));
        profile.OnKey(new RawKeyEvent(K, false, 1, 0));
        profile.OnKey(Down(LeftCtrl));

        Assert.True(profile.IsCapturing);
        Assert.StartsWith("Ctrl, Alt, Windows and F12 cannot be mapped.", profile.Message);

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

        profile.AddAxis.Execute(null);
        profile.OnKey(Down(J));
        profile.OnKey(Down(K));

        var axis = profile.SelectedRow!;
        Assert.True(axis.IsAxis);
        Assert.Equal(J, axis.NegativeKey);
        Assert.Equal(K, axis.Key);
    }

    [Fact]
    public void The_preview_follows_the_key_in_counts_and_depth()
    {
        _h.ChooseTkl();
        var profile = Create();
        profile.IsOpen = true;
        Assert.Equal(DefaultProfiles.Key.W, profile.SelectedRow!.Key);
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

        _h.Workspace.Session = SessionState.Starting;
        Assert.False(_h.Sensor.Wanted);
        Assert.Null(profile.Marker);
    }

    [Fact]
    public async Task Saving_the_running_profile_stops_the_session_only_when_the_saved_text_changed()
    {
        var profile = Create();
        _h.Workspace.RunningProfileText = ProfileJson.Serialize(_h.Workspace.ActiveProfile!);
        _h.Workspace.Session = SessionState.Running;

        await profile.SaveAsync();
        Assert.Empty(_h.Session.Stops);

        profile.SelectedRow!.Deadzone = 0.1;
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
