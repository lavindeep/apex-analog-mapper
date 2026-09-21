using ApexMapper.Windows.Input;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

public class ForegroundResolverTests
{
    private const string Forza = @"C:\Games\ForzaHorizon6\ForzaHorizon6.exe";
    private const string FrameHost = @"C:\Windows\System32\ApplicationFrameHost.exe";

    /// <summary>A window tree: window to process id, process id to image path and elevation, frame host to CoreWindow child.</summary>
    private sealed class FakeWindows : IWindowSystem
    {
        public Dictionary<nint, uint> Owners { get; } = new();

        public Dictionary<uint, string> Paths { get; } = new();

        public Dictionary<uint, bool?> Elevation { get; } = new();

        public Dictionary<nint, nint> CoreWindows { get; } = new();

        public uint ProcessIdOf(nint window) => Owners.GetValueOrDefault(window);

        public string? ImagePathOf(uint processId) => Paths.GetValueOrDefault(processId);

        public nint CoreWindowChildOf(nint window) => CoreWindows.GetValueOrDefault(window);

        public bool? IsElevated(uint processId) => Elevation.TryGetValue(processId, out var value) ? value : false;
    }

    [Fact]
    public void A_plain_game_window_matches_by_executable_path_regardless_of_case()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 4242;
        windows.Paths[4242] = Forza;

        var info = ForegroundResolver.Resolve(windows, 100, Forza.ToUpperInvariant());

        Assert.True(info.IsGame);
        Assert.True(info.GameHasFocus);
        Assert.False(info.Unwrapped);
        Assert.Equal(4242u, info.ProcessId);
    }

    [Fact]
    public void The_match_survives_a_restart_with_a_new_process_id()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 4242;
        windows.Paths[4242] = Forza;
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
    public void An_elevated_game_is_reported_and_does_not_have_focus_for_the_hook()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 4242;
        windows.Paths[4242] = Forza;
        windows.Elevation[4242] = true;

        var info = ForegroundResolver.Resolve(windows, 100, Forza);

        Assert.True(info.IsGame);
        Assert.True(info.Elevated);
        Assert.False(info.GameHasFocus);
    }

    [Fact]
    public void An_unreadable_token_counts_as_elevated()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 4242;
        windows.Paths[4242] = Forza;
        windows.Elevation[4242] = null;

        Assert.True(ForegroundResolver.Resolve(windows, 100, Forza).Elevated);
    }

    [Fact]
    public void Another_window_no_game_or_no_window_is_not_the_game()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 4242;
        windows.Paths[4242] = Forza;
        windows.Owners[300] = 7;
        windows.Paths[7] = @"C:\Windows\explorer.exe";
        windows.Elevation[7] = true;

        var other = ForegroundResolver.Resolve(windows, 300, Forza);
        Assert.False(other.IsGame);
        Assert.False(other.Elevated);
        Assert.False(ForegroundResolver.Resolve(windows, 100, null).IsGame);
        Assert.Equal(ForegroundInfo.None, ForegroundResolver.Resolve(windows, 0, Forza));
        Assert.False(ForegroundResolver.Resolve(windows, 999, Forza).IsGame);
    }
}
