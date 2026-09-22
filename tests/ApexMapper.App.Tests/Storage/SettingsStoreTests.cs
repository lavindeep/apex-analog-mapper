using System.IO;
using ApexMapper.App.Storage;
using Xunit;

namespace ApexMapper.App.Tests.Storage;

public class SettingsStoreTests
{
    [Fact]
    public void Nothing_saved_loads_the_defaults_quietly()
    {
        using var dir = new TempDirectory();

        var (settings, problem) = new SettingsStore(dir.File("settings.json")).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.Null(problem);
    }

    [Fact]
    public void Settings_round_trip()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("settings.json"));
        var saved = new AppSettings(Guid.NewGuid(), @"C:\Games\ForzaHorizon6\ForzaHorizon6.exe", "forza", Prereleases: true);

        store.Save(saved);

        Assert.Equal((saved, (string?)null), store.Load());
    }

    [Fact]
    public void A_file_missing_members_reads_them_as_defaults()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("settings.json"), """{ "version": 1, "payload": { "active_profile": "forza" } }""");

        var (settings, problem) = new SettingsStore(dir.File("settings.json")).Load();

        Assert.Equal(new AppSettings(ActiveProfile: "forza"), settings);
        Assert.Null(problem);
    }

    [Fact]
    public void An_unreadable_file_loads_the_defaults_and_says_so()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("settings.json"), "{ not json");

        var (settings, problem) = new SettingsStore(dir.File("settings.json")).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.Contains("settings file could not be read", problem);
        Assert.True(File.Exists(dir.File("settings.json.corrupt")));
    }
}
