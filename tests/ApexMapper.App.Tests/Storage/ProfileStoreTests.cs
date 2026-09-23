using System.IO;
using ApexMapper.App.Storage;
using ApexMapper.Core.Bindings;
using ApexMapper.Core.Profiles;
using ApexMapper.Core.Storage;
using Xunit;

namespace ApexMapper.App.Tests.Storage;

public class ProfileStoreTests
{
    private static readonly string ForzaText = ProfileJson.Serialize(DefaultProfiles.Forza());

    /// <summary>Forza with W moved to the left trigger and S to the right, under another id and name.</summary>
    private static Profile Custom(string id = "drift", string name = "Drift") =>
        DefaultProfiles.Forza() with
        {
            Id = id,
            Name = name,
            Keys = [.. DefaultProfiles.Forza().Keys.Select(k => k.Target switch
            {
                PadTarget.RightTrigger => k with { Target = PadTarget.LeftTrigger },
                PadTarget.LeftTrigger => k with { Target = PadTarget.RightTrigger },
                _ => k,
            })],
        };

    /// <summary>The same profile as a newer version of the app would write it.</summary>
    private static string Newer(Profile profile) => ProfileJson.Serialize(profile).Replace("\"version\": 1", "\"version\": 2");

    [Fact]
    public void On_a_first_run_the_folder_and_the_forza_profile_are_created()
    {
        using var dir = new TempDirectory();
        var folder = Path.Combine(dir.Path, "profiles");

        var entries = new ProfileStore(folder).List();

        Assert.Equal(["forza"], entries.Select(e => e.Id));
        Assert.Null(entries[0].Problem);
        Assert.Equal(ForzaText, File.ReadAllText(Path.Combine(folder, "forza.json")));
    }

    [Fact]
    public void Forza_is_listed_first_and_the_rest_by_name()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        // Ordinal order would put "Zeta" first: names sort the way a person reads them.
        store.Save(Custom("a", "Zeta"));
        store.Save(Custom("b", "alpha"));

        var entries = store.List();

        Assert.Equal(["forza", "b", "a"], entries.Select(e => e.Id));
        Assert.All(entries, e => Assert.Null(e.Problem));
    }

    [Fact]
    public void An_edited_forza_profile_is_kept()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        store.Save(DefaultProfiles.Forza() with { Name = "My Forza" });

        Assert.Equal("My Forza", store.Load("forza")!.Profile!.Name);
        Assert.Equal("My Forza", store.List()[0].Profile!.Name);
    }

    [Fact]
    public void A_saved_profile_round_trips_and_saving_it_unchanged_writes_nothing()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        var drift = Custom();

        Assert.True(store.Save(drift));
        Assert.False(store.Save(Custom()), "the same content is not an edit");
        Assert.False(File.Exists(dir.File("drift.json.bak")), "an unchanged save must not touch the file");
        Assert.True(store.Save(drift with { Name = "Drift 2" }));

        var loaded = store.Load("drift")!;
        Assert.Null(loaded.Problem);
        Assert.Equal(ProfileJson.Serialize(drift with { Name = "Drift 2" }), ProfileJson.Serialize(loaded.Profile!));
        Assert.Null(store.Load("missing"));
    }

    [Fact]
    public void Deleting_removes_the_profile_and_its_backup_and_the_forza_profile_cannot_be_deleted()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        store.Save(Custom());
        store.Save(Custom() with { Name = "Renamed" });
        Assert.True(File.Exists(dir.File("drift.json.bak")));

        Assert.True(store.Delete("drift"));
        Assert.False(store.Delete("forza"));

        Assert.Null(store.Load("drift"));
        Assert.False(File.Exists(dir.File("drift.json.bak")));
        Assert.Equal(["forza"], store.List().Select(e => e.Id));
    }

    [Fact]
    public void A_delete_that_cannot_remove_the_backup_leaves_the_profile_whole()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        store.Save(Custom());
        store.Save(Custom() with { Name = "Renamed" });

        using (new FileStream(dir.File("drift.json.bak"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => store.Delete("drift"));
        }

        var drift = store.Load("drift")!;
        Assert.Equal("Renamed", drift.Profile!.Name);
        Assert.Null(drift.Problem);
    }

    [Fact]
    public void Reset_gives_a_profile_the_forza_bindings_under_its_own_id_and_name_and_says_whether_it_changed()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        store.Save(Custom());
        store.Save(DefaultProfiles.Forza() with { Name = "My Forza", Keys = Custom().Keys });

        var drift = store.Reset("drift");
        var forza = store.Reset("forza");
        var again = store.Reset("forza");
        var missing = store.Reset("nope");

        Assert.True(drift.Changed);
        Assert.Equal(ProfileJson.Serialize(DefaultProfiles.Forza() with { Id = "drift", Name = "Drift" }), ProfileJson.Serialize(drift.Profile));
        Assert.True(forza.Changed);
        Assert.Equal(ForzaText, ProfileJson.Serialize(forza.Profile));
        Assert.Equal(ForzaText, File.ReadAllText(dir.File("forza.json")));
        Assert.False(again.Changed);
        Assert.Equal("nope", missing.Profile.Name);
    }

    [Fact]
    public void An_unreadable_forza_profile_is_reset_kept_aside_and_reported()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("forza.json"), "{ not json");

        var forza = new ProfileStore(dir.Path).List()[0];

        Assert.Equal(ForzaText, ProfileJson.Serialize(forza.Profile!));
        Assert.Contains("reset to the default", forza.Problem);
        Assert.Contains("not valid JSON", forza.Problem);
        Assert.Equal("{ not json", File.ReadAllText(dir.File("forza.json.corrupt")));
    }

    [Fact]
    public void A_locked_forza_profile_is_stood_in_for_by_the_default_and_left_alone()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        store.Save(DefaultProfiles.Forza() with { Name = "My Forza" });
        var before = File.ReadAllText(dir.File("forza.json"));

        using (new FileStream(dir.File("forza.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var forza = store.List()[0];
            Assert.Equal("Forza", forza.Profile!.Name);
            Assert.Contains("could not be opened", forza.Problem);
            Assert.Throws<IOException>(() => store.Save(DefaultProfiles.Forza() with { Name = "Other" }));
        }

        // The lock is gone, but what the window holds is the stand-in: saving it would replace the user's Forza.
        Assert.Throws<IOException>(() => store.Save(DefaultProfiles.Forza() with { Name = "Other" }));
        Assert.Equal(before, File.ReadAllText(dir.File("forza.json")));
        Assert.Equal("My Forza", store.Load("forza")!.Profile!.Name);
        Assert.True(store.Save(DefaultProfiles.Forza() with { Name = "Other" }));
    }

    [Fact]
    public void A_profile_that_cannot_be_read_now_is_not_saved_over_even_where_the_lock_allows_it()
    {
        using var dir = new TempDirectory();
        new ProfileStore(dir.Path).Save(Custom());
        var before = File.ReadAllText(dir.File("drift.json"));

        // Unreadable, yet replaceable: a store that never loaded it must still refuse.
        using (new FileStream(dir.File("drift.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Throws<IOException>(() => new ProfileStore(dir.Path).Save(Custom() with { Name = "Other" }));
        }

        Assert.Equal(before, File.ReadAllText(dir.File("drift.json")));
    }

    [Fact]
    public void A_forza_profile_that_cannot_be_written_is_still_listed_from_the_default()
    {
        using var dir = new TempDirectory();
        // A file where the folder should be: nothing can be written under it.
        File.WriteAllText(dir.File("profiles"), "");

        var entries = new ProfileStore(dir.File("profiles")).List();

        var forza = Assert.Single(entries);
        Assert.Equal(ForzaText, ProfileJson.Serialize(forza.Profile!));
        Assert.Contains("could not be saved", forza.Problem);
    }

    [Fact]
    public void Files_from_a_newer_version_are_listed_with_the_reason_and_never_written_over()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        File.WriteAllText(dir.File("drift.json"), Newer(Custom()));
        File.WriteAllText(dir.File("forza.json"), Newer(DefaultProfiles.Forza() with { Name = "Newer Forza" }));

        var entries = store.List();

        Assert.Equal("Forza", entries[0].Profile!.Name);
        Assert.Contains("newer version", entries[0].Problem);
        var drift = Assert.Single(entries, e => e.Id == "drift");
        Assert.Null(drift.Profile);
        Assert.Contains("newer version", drift.Problem);
        Assert.Throws<IOException>(() => store.Save(Custom()));
        Assert.Throws<IOException>(() => store.Save(DefaultProfiles.Forza()));
        Assert.Throws<IOException>(() => store.Reset("drift"));
        Assert.Equal(Newer(Custom()), File.ReadAllText(dir.File("drift.json")));
        Assert.Contains("Newer Forza", File.ReadAllText(dir.File("forza.json")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.corrupt"));
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void An_unreadable_custom_profile_is_listed_without_a_profile_and_with_the_reason()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        File.WriteAllText(dir.File("broken.json"), "{ not json");

        var broken = Assert.Single(store.List(), e => e.Id == "broken");

        Assert.Null(broken.Profile);
        Assert.Contains("could not be read", broken.Problem);
        Assert.Contains("not valid JSON", broken.Problem);
    }

    [Fact]
    public void The_file_name_is_the_id_whatever_its_case_and_other_names_are_left_alone()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        File.WriteAllText(dir.File("copied.json"), ProfileJson.Serialize(Custom("drift", "Copied")));
        File.WriteAllText(dir.File("Shared.JSON"), ProfileJson.Serialize(Custom("shared", "Shared")));
        File.WriteAllText(dir.File("Not An Id.json"), ProfileJson.Serialize(Custom("other", "Other")));
        store.Save(Custom());

        Assert.Equal(["forza", "copied", "drift", "shared"], store.List().Select(e => e.Id));
        var copied = store.Load("copied")!.Profile!;
        Assert.Equal("copied", copied.Id);
        Assert.False(store.Save(copied), "saving a copied file unchanged is not an edit");
        Assert.Throws<ArgumentException>(() => store.Load(@"..\settings"));
        Assert.Throws<ArgumentException>(() => store.Save(Custom(@"..\escape")));
        Assert.Throws<ArgumentException>(() => store.Delete("C:"));
    }

    [Theory]
    [InlineData("drift", true)]
    [InlineData("rally-2_wet", true)]
    [InlineData("console", true)]
    [InlineData("drift\n", false)]
    [InlineData("Drift", false)]
    [InlineData("-drift", false)]
    [InlineData("drift.v2", false)]
    [InlineData("con", false)]
    [InlineData("nul", false)]
    [InlineData("com1", false)]
    [InlineData("lpt9", false)]
    [InlineData("", false)]
    public void Ids_are_plain_file_names_windows_does_not_reserve(string id, bool valid) =>
        Assert.Equal(valid, ProfileStore.IsValidId(id));

    [Fact]
    public void Ids_are_at_most_sixty_four_characters()
    {
        Assert.True(ProfileStore.IsValidId(new string('a', 64)));
        Assert.False(ProfileStore.IsValidId(new string('a', 65)));
    }

    [Fact]
    public void An_invalid_profile_is_refused_before_anything_is_written()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        var twice = Custom() with { Keys = [.. Custom().Keys, Custom().Keys[0]] };

        Assert.Throws<ArgumentException>(() => store.Save(twice));
        Assert.False(File.Exists(dir.File("drift.json")));
    }

    [Fact]
    public void A_damaged_profile_is_restored_from_its_backup_and_reported()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        store.Save(Custom());
        store.Save(Custom() with { Name = "Newer" });
        File.WriteAllText(dir.File("drift.json"), "{ not json");

        var drift = store.Load("drift")!;

        Assert.Equal("Drift", drift.Profile!.Name);
        Assert.Equal("The profile \"drift\" file was missing or damaged, so its backup was used. The damaged file was kept as drift.json.corrupt.", drift.Problem);
        Assert.True(File.Exists(JsonFile.CorruptPath(dir.File("drift.json"))));
    }
}
