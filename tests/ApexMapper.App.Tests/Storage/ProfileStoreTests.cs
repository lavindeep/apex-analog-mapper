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

    [Fact]
    public void The_forza_profile_is_written_when_missing_and_listed_first()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        store.Save(Custom("a-first", "Aardvark"));

        var entries = store.List();

        Assert.Equal(["forza", "a-first"], entries.Select(e => e.Id));
        Assert.Equal(ForzaText, File.ReadAllText(dir.File("forza.json")));
        Assert.All(entries, e => Assert.Null(e.Problem));
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
    public void Reset_gives_a_profile_the_forza_bindings_under_its_own_id_and_name()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        store.Save(Custom());
        store.Save(DefaultProfiles.Forza() with { Name = "My Forza", Keys = Custom().Keys });

        var drift = store.Reset("drift");
        var forza = store.Reset("forza");

        Assert.Equal(ProfileJson.Serialize(DefaultProfiles.Forza() with { Id = "drift", Name = "Drift" }), ProfileJson.Serialize(drift));
        Assert.Equal(ForzaText, ProfileJson.Serialize(forza));
        Assert.Equal(ForzaText, File.ReadAllText(dir.File("forza.json")));
    }

    [Fact]
    public void An_unreadable_forza_profile_is_reset_kept_aside_and_reported()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("forza.json"), "{ not json");

        var forza = new ProfileStore(dir.Path).List()[0];

        Assert.Equal(ForzaText, ProfileJson.Serialize(forza.Profile!));
        Assert.Contains("reset to the default", forza.Problem);
        Assert.Equal("{ not json", File.ReadAllText(dir.File("forza.json.corrupt")));
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
    }

    [Fact]
    public void The_file_name_is_the_id_and_names_that_are_not_plain_ids_are_left_alone()
    {
        using var dir = new TempDirectory();
        var store = new ProfileStore(dir.Path);
        File.WriteAllText(dir.File("copied.json"), ProfileJson.Serialize(Custom("drift", "Copied")));
        File.WriteAllText(dir.File("Not An Id.json"), ProfileJson.Serialize(Custom("other", "Other")));
        store.Save(Custom());

        Assert.Equal(["forza", "copied", "drift"], store.List().Select(e => e.Id));
        Assert.Equal("copied", store.Load("copied")!.Profile!.Id);
        Assert.Throws<ArgumentException>(() => store.Load(@"..\settings"));
        Assert.Throws<ArgumentException>(() => store.Save(Custom(@"..\escape")));
        Assert.Throws<ArgumentException>(() => store.Delete("C:"));
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
        Assert.Contains("restored from its backup", drift.Problem);
        Assert.True(File.Exists(JsonFile.CorruptPath(dir.File("drift.json"))));
    }
}
