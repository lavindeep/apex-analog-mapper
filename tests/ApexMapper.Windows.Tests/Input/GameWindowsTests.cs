using ApexMapper.Core.Keys;
using ApexMapper.Windows.Input;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

public class GameWindowsTests
{
    [Fact]
    public void One_entry_per_executable_sorted_by_title_without_this_app()
    {
        var windows = FakeWindows.WithGameAndDesktop();
        windows.Owners[101] = 4242;
        windows.Owners[500] = 55;
        windows.Paths[55] = @"C:\Apps\ApexAnalogMapper.exe";

        var list = GameWindows.From(windows, [(100, "Forza Horizon 6"), (101, "Forza Horizon 6 launcher"), (300, "Documents"), (500, "Apex Analog Mapper")], ownProcessId: 55);

        Assert.Equal([("Documents", FakeWindows.Explorer), ("Forza Horizon 6", FakeWindows.Forza)], list.Select(w => (w.Title, w.ImagePath)));
    }

    [Fact]
    public void A_store_game_is_listed_under_its_own_executable_with_its_elevation()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 500;
        windows.Paths[500] = FakeWindows.FrameHost;
        windows.CoreWindows[100] = 101;
        windows.Owners[101] = 4242;
        windows.Paths[4242] = FakeWindows.Forza;
        windows.Elevation[4242] = true;

        var game = Assert.Single(GameWindows.From(windows, [(100, "Forza Horizon 6")], ownProcessId: 1));

        Assert.Equal(FakeWindows.Forza, game.ImagePath);
        Assert.Equal(Elevation.Elevated, game.Elevation);
    }

    [Fact]
    public void Windows_whose_process_cannot_be_read_are_left_out()
    {
        var windows = new FakeWindows();
        windows.Owners[100] = 9;

        Assert.Empty(GameWindows.From(windows, [(100, "Protected")], ownProcessId: 1));
    }

    /// <summary>The real enumeration only reads window state; it must run and never list this process.</summary>
    [Fact]
    public void The_real_list_runs_and_leaves_this_process_out()
    {
        var own = Environment.ProcessPath!;

        Assert.DoesNotContain(GameWindows.List(), w => string.Equals(w.ImagePath, own, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Key_names_come_from_the_layout_and_tell_extended_keys_apart()
    {
        var up = KeyNames.Of(new ScanCode(0xE048));
        var numpad8 = KeyNames.Of(new ScanCode(0x48));

        Assert.False(string.IsNullOrWhiteSpace(KeyNames.Of(new ScanCode(0x11))));
        Assert.NotEqual(up, numpad8);
        Assert.Equal("0xE11D", KeyNames.Of(new ScanCode(0xE11D)));
    }
}
