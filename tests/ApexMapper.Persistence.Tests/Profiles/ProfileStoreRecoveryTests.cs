using ApexMapper.Core.Engine;
using ApexMapper.Core.Pipeline;
using ApexMapper.Persistence.Profiles;
using ApexMapper.Persistence.Recovery;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Profiles;

public class ProfileStoreRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-store-rec-" + Guid.NewGuid().ToString("N"));

    public ProfileStoreRecoveryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Path_ => System.IO.Path.Combine(_dir, "racing.json");

    private static Profile Make(string id) => new(
        Id: id,
        Name: id,
        Device: new DeviceMatcher(1, 1, null, null),
        Game: new GameMatcher(null, null, null),
        Activation: ActivationPolicy.Default,
        SingleBindings: Array.Empty<SingleKeyBinding>(),
        AxisBindings: Array.Empty<AxisPairBinding>(),
        Notes: null);

    [Fact]
    public void Corrupt_primary_recovers_from_backup_and_quarantines_the_corrupt_file()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 5));
        store.Save(Make("racing"));
        store.Save(Make("racing")); // bak.1 is now a good copy
        File.WriteAllText(Path_, "{ not valid json");

        var loaded = store.LoadAll(out var reports);

        loaded.Should().ContainSingle().Which.Id.Should().Be("racing");
        File.Exists(Path_ + ".corrupt").Should().BeTrue("corrupt evidence must be preserved");
        Store_Parses(Path_).Should().BeTrue("the primary must be restored to a valid profile");
        reports.Should().ContainSingle();
        reports[0].Outcome.Should().Be(RecoveryOutcome.RecoveredFromBackup);
    }

    [Fact]
    public void Recovery_cascades_to_a_deeper_backup_when_earlier_ones_are_corrupt()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 5));
        store.Save(Make("racing"));
        store.Save(Make("racing"));
        store.Save(Make("racing")); // bak.1 and bak.2 both exist and are good
        File.WriteAllText(Path_, "{ bad");
        File.WriteAllText(Path_ + ".bak.1", "{ bad");

        var loaded = store.LoadAll(out var reports);

        loaded.Should().ContainSingle();
        reports.Should().ContainSingle();
        reports[0].Outcome.Should().Be(RecoveryOutcome.RecoveredFromBackup);
        reports[0].File.Should().EndWith(".bak.2");
        File.Exists(Path_ + ".corrupt").Should().BeTrue();
    }

    [Fact]
    public void All_copies_corrupt_reports_quarantine_and_deletes_nothing()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 5));
        store.Save(Make("racing"));
        store.Save(Make("racing")); // bak.1 exists
        File.WriteAllText(Path_, "{ bad");
        File.WriteAllText(Path_ + ".bak.1", "{ also bad");

        var loaded = store.LoadAll(out var reports);

        loaded.Should().BeEmpty();
        reports.Should().ContainSingle();
        reports[0].Outcome.Should().Be(RecoveryOutcome.Quarantined);
        File.Exists(Path_ + ".corrupt").Should().BeTrue("corrupt primary preserved as evidence");
        File.Exists(Path_ + ".bak.1").Should().BeTrue("corrupt backup must not be deleted");
    }

    private static bool Store_Parses(string path)
        => ProfileStore.Parse(File.ReadAllText(path)).Status == ParseStatus.Ok;
}
