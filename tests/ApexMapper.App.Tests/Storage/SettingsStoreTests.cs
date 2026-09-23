using System.IO;
using ApexMapper.App.Storage;
using Xunit;

namespace ApexMapper.App.Tests.Storage;

public class SettingsStoreTests
{
    private static readonly AppSettings Chosen = new(Guid.NewGuid(), @"C:\Games\ForzaHorizon6\ForzaHorizon6.exe", "drift", Prereleases: true);

    [Fact]
    public void Nothing_saved_loads_the_defaults_quietly()
    {
        using var dir = new TempDirectory();

        var (settings, problem) = new SettingsStore(dir.File("settings.json")).Load();

        Assert.Null(settings.Keyboard);
        Assert.Null(settings.GamePath);
        Assert.Null(settings.ActiveProfile);
        Assert.False(settings.Prereleases, "prereleases are off unless the user turns them on");
        Assert.Null(problem);
    }

    [Fact]
    public void Settings_round_trip()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("settings.json"));

        store.Save(Chosen);

        Assert.Equal((Chosen, (string?)null), store.Load());
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
    public void An_unreadable_file_loads_the_defaults_says_so_and_is_replaced_by_the_next_save()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("settings.json"), "{ not json");
        var store = new SettingsStore(dir.File("settings.json"));

        var (settings, problem) = store.Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.Contains("settings file could not be read", problem);
        Assert.True(File.Exists(dir.File("settings.json.corrupt")));
        store.Save(Chosen);
        Assert.Equal(Chosen, store.Load().Settings);
    }

    [Fact]
    public void Settings_that_could_not_be_opened_are_not_saved_over_until_a_load_reads_them()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        var store = new SettingsStore(path);
        store.Save(Chosen);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (settings, problem) = store.Load();
            Assert.Equal(new AppSettings(), settings);
            Assert.Contains("could not be opened", problem);
        }
        Assert.Throws<IOException>(() => store.Save(new AppSettings(ActiveProfile: "forza")));
        Assert.Equal(Chosen, new SettingsStore(path).Load().Settings);

        Assert.Equal(Chosen, store.Load().Settings);
        store.Save(Chosen with { ActiveProfile = "forza" });
        Assert.Equal("forza", store.Load().Settings.ActiveProfile);
    }

    [Fact]
    public void A_file_from_a_newer_version_loads_the_defaults_and_is_never_written_over()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        var newer = """{ "version": 2, "payload": { "active_profile": "drift", "theme": "dark" } }""";
        File.WriteAllText(path, newer);
        var store = new SettingsStore(path);

        var (settings, problem) = store.Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.Contains("newer version", problem);
        Assert.Throws<IOException>(() => store.Save(settings));
        Assert.Throws<IOException>(() => new SettingsStore(path).Save(settings));
        Assert.Equal(newer, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".corrupt"));
    }

    /// <summary>
    /// The way out the message gives has to work when the backup is the newer file, as
    /// behind a damaged file or after a newer version saved twice: moving only the file
    /// leaves the newer backup in charge.
    /// </summary>
    [Fact]
    public void The_way_out_of_a_newer_file_names_its_backup()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ not json");
        File.WriteAllText(path + ".bak", """{ "version": 2, "payload": {} }""");

        var (_, problem) = new SettingsStore(path).Load();
        File.Delete(path);
        var (_, withoutTheFile) = new SettingsStore(path).Load();

        Assert.Contains("move the file and its .bak copy", problem);
        Assert.Contains("newer version", withoutTheFile);
    }
}
