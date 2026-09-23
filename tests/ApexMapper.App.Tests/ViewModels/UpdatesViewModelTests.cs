using System.Net.Http;
using ApexMapper.App.Model;
using ApexMapper.App.Storage;
using ApexMapper.App.ViewModels;
using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.App.Tests.ViewModels;

public sealed class UpdatesViewModelTests : IDisposable
{
    private readonly AppHarness _h = new();

    public void Dispose() => _h.Dispose();

    private UpdatesViewModel Create(AppHarness? h = null)
    {
        h ??= _h;
        return new UpdatesViewModel(h.Services, h.Workspace, h.SavedSettings);
    }

    [Fact]
    public void A_launch_asks_for_a_newer_version_and_keeps_the_answer_for_six_hours()
    {
        _h.Updates.Newest = "0.5.1";

        var updates = Create();

        Assert.Equal("Version 0.5.1 is available.", updates.Text);
        Assert.Equal("Update", updates.ActText);
        Assert.Equal((_h.Clock, "0.5.1"), (_h.SavedSettings.UpdateCheckedAt, _h.SavedSettings.UpdateFound));
        Assert.Equal("Version 0.5.1 is available", new SetupViewModel(_h.Services, _h.Workspace, _h.SavedSettings).Summary);

        _h.Clock += UpdatesViewModel.CacheFor - TimeSpan.FromMinutes(1);
        _h.Updates.Newest = "0.5.2";
        Assert.Equal("Version 0.5.1 is available.", Create().Text);
        Assert.Single(_h.Updates.Asked);

        _h.Clock += TimeSpan.FromMinutes(1);
        Assert.Equal("Version 0.5.2 is available.", Create().Text);
        Assert.Equal(2, _h.Updates.Asked.Count);
    }

    [Fact]
    public void A_saved_answer_this_copy_has_caught_up_with_reads_as_the_newest()
    {
        _h.Services.Settings.Save(new AppSettings(UpdateCheckedAt: _h.Clock, UpdateFound: "0.5.0"));

        var updates = Create();

        Assert.Empty(_h.Updates.Asked);
        Assert.Equal("This is the newest version.", updates.Text);
        Assert.Equal("Check for updates", updates.ActText);
    }

    [Fact]
    public void A_launch_check_that_fails_is_only_logged_and_one_the_user_asked_for_says_why()
    {
        _h.Updates.Fault = new HttpRequestException("No such host is known.");

        var updates = Create();

        Assert.Null(updates.Text);
        Assert.Contains("Update check failed: No such host is known.", _h.Log);
        Assert.Null(_h.SavedSettings.UpdateCheckedAt);

        updates.Act.Execute(null);
        Assert.Equal("Could not reach GitHub to look for a newer version: No such host is known.", updates.Text);

        _h.Updates.Fault = null;
        updates.Act.Execute(null);
        Assert.Equal("This is the newest version.", updates.Text);
    }

    [Fact]
    public void Update_downloads_and_the_new_version_installs_on_a_restart_that_waits_for_mapping_to_stop()
    {
        _h.Updates.Newest = "0.5.1";
        var updates = Create();
        _h.Updates.Hold = new TaskCompletionSource();

        updates.Act.Execute(null);
        _h.Updates.Progress!(42);
        Assert.Equal("Downloading version 0.5.1: 42%.", updates.Text);
        Assert.Equal("Downloading version 0.5.1.", updates.Spoken);
        Assert.Null(updates.ActText);
        _h.Workspace.Session = SessionState.Running;
        _h.Updates.Hold.SetResult();

        Assert.Equal("Restart and update", updates.ActText);
        Assert.EndsWith("installs the next time the app starts. Stop mapping first.", updates.Text);
        Assert.False(updates.Act.CanExecute(null));

        _h.Workspace.Session = SessionState.Idle;
        updates.Act.Execute(null);
        Assert.Equal((1, 1), (_h.Updates.Installs, _h.Closes));
        Assert.Equal("Closing to install version 0.5.1.", updates.Text);
        Assert.False(updates.Act.CanExecute(null));
    }

    [Fact]
    public void An_update_cannot_start_while_mapping_and_a_failed_download_can_be_tried_again()
    {
        _h.Updates.Newest = "0.5.1";
        var updates = Create();
        _h.Workspace.Session = SessionState.Running;

        Assert.Equal("Version 0.5.1 is available. Stop mapping to update.", updates.Text);
        Assert.False(updates.Act.CanExecute(null));

        _h.Workspace.Session = SessionState.Idle;
        _h.Updates.Fault = new HttpRequestException("The connection was reset.");
        updates.Act.Execute(null);
        Assert.Equal("The download failed: The connection was reset.", updates.Text);
        Assert.Equal("Update", updates.ActText);
        Assert.Equal(0, _h.Updates.Installs);
    }

    [Fact]
    public void A_release_offers_test_versions_only_when_asked_and_a_test_version_always_does()
    {
        var updates = Create();
        Assert.True(updates.CanChoosePrereleases);
        Assert.Equal([false], _h.Updates.Asked);

        updates.Prereleases = true;
        Assert.True(_h.SavedSettings.Prereleases);
        Assert.Equal([false, true], _h.Updates.Asked);

        using var alpha = new AppHarness("0.5.0-alpha");
        var test = Create(alpha);
        Assert.False(test.CanChoosePrereleases);
        Assert.Equal([true], alpha.Updates.Asked);
    }

    [Fact]
    public void Turning_test_versions_off_while_github_answers_asks_again()
    {
        _h.Services.Settings.Save(new AppSettings(Prereleases: true));
        _h.Updates.NewestTest = "0.5.1-beta.1";
        var answer = _h.Updates.Hold = new TaskCompletionSource();
        var updates = Create();

        updates.Prereleases = false;
        _h.Updates.Hold = null;
        answer.SetResult();

        Assert.Equal([true, false], _h.Updates.Asked);
        Assert.Equal("This is the newest version.", updates.Text);
        Assert.Null(_h.SavedSettings.UpdateFound);
    }

    [Fact]
    public void With_the_launch_check_off_the_app_looks_only_when_asked()
    {
        _h.Updates.Newest = "0.5.1";
        var updates = Create();
        updates.CheckOnLaunch = false;
        Assert.False(_h.SavedSettings.CheckForUpdates);
        _h.Clock += UpdatesViewModel.CacheFor;

        updates = Create();
        Assert.Single(_h.Updates.Asked);
        Assert.Null(updates.Text);

        updates.Act.Execute(null);
        Assert.Equal("Version 0.5.1 is available.", updates.Text);
    }

    [Fact]
    public void Settings_that_could_not_be_read_hold_back_the_launch_check()
    {
        var updates = new UpdatesViewModel(_h.Services, new Workspace { SettingsUnread = true }, new AppSettings());

        Assert.Empty(_h.Updates.Asked);

        updates.Act.Execute(null);
        Assert.Single(_h.Updates.Asked);
    }

    [Fact]
    public void A_copy_that_was_not_installed_never_looks()
    {
        _h.Updates.Installed = false;

        var updates = Create();

        Assert.Empty(_h.Updates.Asked);
        Assert.Null(updates.ActText);
        Assert.False(updates.CanChoosePrereleases);
        Assert.Equal("This copy was not installed with Setup.exe, so it cannot update itself.", updates.Text);
    }
}
