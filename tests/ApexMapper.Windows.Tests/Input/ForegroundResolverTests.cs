using ApexMapper.Windows.Input;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

public class ForegroundResolverTests
{
    private const string Forza = FakeWindows.Forza;
    private const string FrameHost = FakeWindows.FrameHost;

    [Fact]
    public void A_plain_game_window_matches_by_executable_path_regardless_of_case()
    {
        var windows = FakeWindows.WithGameAndDesktop();

        var info = ForegroundResolver.Resolve(windows, 100, Forza.ToUpperInvariant());

        Assert.True(info.IsGame);
        Assert.True(info.GameHasFocus);
        Assert.False(info.Unwrapped);
        Assert.Equal(4242u, info.ProcessId);
    }

    [Fact]
    public void The_match_survives_a_restart_with_a_new_process_id()
    {
        var windows = FakeWindows.WithGameAndDesktop();
        windows.Owners[200] = 9999;
        windows.Paths[9999] = Forza;

        Assert.True(ForegroundResolver.Resolve(windows, 100, Forza).IsGame);
        Assert.True(ForegroundResolver.Resolve(windows, 200, Forza).IsGame);
    }

    [Fact]
    public void A_frame_host_window_is_unwrapped_to_the_core_window_owner()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 500;
        windows.Paths[500] = FrameHost;
        windows.CoreWindows[100] = 101;
        windows.Owners[101] = 4242;
        windows.Paths[4242] = Forza;

        var info = ForegroundResolver.Resolve(windows, 100, Forza);

        Assert.True(info.IsGame);
        Assert.True(info.Unwrapped);
        Assert.Equal(4242u, info.ProcessId);
        Assert.Equal(Forza, info.ImagePath);
    }

    [Fact]
    public void A_frame_host_window_without_a_core_window_stays_the_frame_host()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 500;
        windows.Paths[500] = FrameHost;

        var info = ForegroundResolver.Resolve(windows, 100, Forza);

        Assert.False(info.IsGame);
        Assert.False(info.Unwrapped);
        Assert.Equal(FrameHost, info.ImagePath);
    }

    [Fact]
    public void An_elevated_game_is_invisible_only_while_this_process_is_not_elevated()
    {
        var windows = FakeWindows.WithGameAndDesktop();
        windows.Elevation[4242] = true;

        var fromUser = ForegroundResolver.Resolve(windows, 100, Forza);
        windows.OwnElevation = true;
        var fromAdmin = ForegroundResolver.Resolve(windows, 100, Forza);

        Assert.True(fromUser.IsGame);
        Assert.Equal(Elevation.Elevated, fromUser.Elevation);
        Assert.False(fromUser.GameHasFocus);
        Assert.Equal(Elevation.Visible, fromAdmin.Elevation);
        Assert.True(fromAdmin.GameHasFocus);
    }

    [Fact]
    public void An_unreadable_own_token_counts_as_not_elevated()
    {
        var windows = FakeWindows.WithGameAndDesktop();
        windows.Elevation[4242] = true;
        windows.OwnElevation = null;

        Assert.Equal(Elevation.Elevated, ForegroundResolver.Resolve(windows, 100, Forza).Elevation);
    }

    [Fact]
    public void An_unreadable_game_token_is_its_own_state_and_does_not_give_focus()
    {
        var windows = FakeWindows.WithGameAndDesktop();
        windows.Elevation[4242] = null;

        var info = ForegroundResolver.Resolve(windows, 100, Forza);

        Assert.Equal(Elevation.Unknown, info.Elevation);
        Assert.False(info.GameHasFocus);
    }

    [Fact]
    public void Another_window_no_game_or_no_window_is_not_the_game()
    {
        var windows = FakeWindows.WithGameAndDesktop();
        windows.Elevation[7] = true;

        var other = ForegroundResolver.Resolve(windows, 300, Forza);
        Assert.False(other.IsGame);
        Assert.Equal(Elevation.Visible, other.Elevation);
        Assert.False(ForegroundResolver.Resolve(windows, 100, null).IsGame);
        Assert.Equal(ForegroundInfo.None, ForegroundResolver.Resolve(windows, 0, Forza));
        Assert.False(ForegroundResolver.Resolve(windows, 999, Forza).IsGame);
    }
}
