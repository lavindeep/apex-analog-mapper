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

    [Fact]
    public void Missing_primary_with_intact_backup_is_recovered()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 5));
        store.Save(Make("racing"));
        store.Save(Make("racing")); // bak.1 is now a good copy
        File.Delete(Path_);

        var loaded = store.LoadAll(out var reports);

        loaded.Should().ContainSingle().Which.Id.Should().Be("racing");
        Store_Parses(Path_).Should().BeTrue("the primary must be restored from the backup");
        reports.Should().ContainSingle();
        reports[0].Outcome.Should().Be(RecoveryOutcome.RecoveredFromBackup);
    }

    [Fact]
    public void Primary_stranded_as_quarantine_with_intact_backup_is_recovered()
    {
        // Crash window: the quarantine rename completed but the backup restore never ran.
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 5));
        store.Save(Make("racing"));
        store.Save(Make("racing")); // bak.1 is now a good copy
        File.Move(Path_, Path_ + ".corrupt");

        var loaded = store.LoadAll(out var reports);

        loaded.Should().ContainSingle().Which.Id.Should().Be("racing");
        Store_Parses(Path_).Should().BeTrue("the primary must be restored from the backup");
        File.Exists(Path_ + ".corrupt").Should().BeTrue("the stranded quarantine evidence must be preserved");
        reports.Should().ContainSingle();
        reports[0].Outcome.Should().Be(RecoveryOutcome.RecoveredFromBackup);
    }

    [Fact]
    public void LoadAll_reports_newer_schema_files_and_leaves_them_untouched()
    {
        var newer = "{\"version\": 999, \"payload\": null}";
        File.WriteAllText(Path_, newer);
        var store = new ProfileStore(new ProfileStoreOptions(_dir));

        var loaded = store.LoadAll(out var reports);

        loaded.Should().BeEmpty();
        reports.Should().ContainSingle();
        reports[0].Outcome.Should().Be(RecoveryOutcome.NewerSchema);
        File.Exists(Path_ + ".corrupt").Should().BeFalse("a newer-schema file is not corrupt");
        File.ReadAllText(Path_).Should().Be(newer, "the file must be left untouched");
    }

    [Fact]
    public void Newer_schema_with_payload_this_build_cannot_read_is_not_treated_as_corrupt()
    {
        var newer = "{\"version\":3,\"payload\":{\"id\":42}}";
        File.WriteAllText(Path_, newer);
        var store = new ProfileStore(new ProfileStoreOptions(_dir));

        var loaded = store.LoadAll(out var reports);

        loaded.Should().BeEmpty();
        reports.Should().ContainSingle();
        reports[0].Outcome.Should().Be(RecoveryOutcome.NewerSchema);
        File.Exists(Path_ + ".corrupt").Should().BeFalse("a newer-schema file must not be quarantined");
        File.ReadAllText(Path_).Should().Be(newer, "the file must be left untouched");

        var act = () => store.Save(Make("racing"));
        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path_).Should().Be(newer, "the newer file must not be downgraded");
        File.Exists(Path_ + ".bak.1").Should().BeFalse("no backup generation should be consumed");
    }

    [Theory]
    [InlineData("{\"version\":0,\"payload\":null}")]
    [InlineData("{\"version\":-1,\"payload\":null}")]
    [InlineData("{\"version\":\"garbage\",\"payload\":null}")]
    [InlineData("{\"version\":null,\"payload\":null}")]
    [InlineData("{\"payload\":null}")]
    [InlineData("{\"version\":1,\"payload\":null}")]
    [InlineData("not json at all")]
    public void Parse_classifies_invalid_version_headers_and_null_current_payloads_as_corrupt(string text)
        => ProfileStore.Parse(text).Status.Should().Be(ParseStatus.Corrupt);

    [Theory]
    [InlineData("{\"version\":999,\"payload\":null}")]
    [InlineData("{\"version\":3,\"payload\":{\"id\":42}}")]
    public void Parse_classifies_any_newer_version_as_newer_schema_regardless_of_payload(string text)
        => ProfileStore.Parse(text).Status.Should().Be(ParseStatus.NewerSchema);

    [Fact]
    public void Parse_classifies_a_throwing_migration_step_as_corrupt()
    {
        var v1 = "{\"version\":1,\"payload\":null}";
        ProfileStore.Parse(v1, (_, _, _) => throw new System.Text.Json.JsonException("bad"))
            .Status.Should().Be(ParseStatus.Corrupt);
        ProfileStore.Parse(v1, (_, _, _) => throw new ArgumentException("bad"))
            .Status.Should().Be(ParseStatus.Corrupt);
        ProfileStore.Parse(v1, (_, _, _) => throw new InvalidOperationException("bad"))
            .Status.Should().Be(ParseStatus.Corrupt);
    }

    private static string DocWithCurve(string curveJson) => $$"""
        {
          "version": 2,
          "payload": {
            "id": "curved", "name": "Curved",
            "device": { "vendor_id": 1, "product_id": 1, "serial_number": null, "product_name_pattern": null },
            "game": { "executable_name": null, "window_title_pattern": null, "steam_app_id": null },
            "activation": { "scope": "foreground_only", "focus_loss_debounce_ms": 500, "auto_enable": false, "requires_opt_in_for_protected_games": true },
            "single_bindings": [
              { "source": { "scan_code": 17 }, "target": "right_trigger", "curve": {{curveJson}}, "press_ramp_ms": 0, "release_ramp_ms": 0 }
            ],
            "axis_bindings": [], "notes": null
          }
        }
        """;

    [Fact]
    public void Parse_accepts_a_valid_monotone_curve()
    {
        var text = DocWithCurve("[[0,0],[0.5,0.3],[1,1]]");
        ProfileStore.Parse(text).Status.Should().Be(ParseStatus.Ok);
    }

    [Theory]
    // More than eight control points.
    [InlineData("[[0,0],[0.1,0.1],[0.2,0.2],[0.3,0.3],[0.4,0.4],[0.5,0.5],[0.6,0.6],[0.7,0.7],[1,1]]")]
    // x not strictly increasing.
    [InlineData("[[0,0],[0.5,0.5],[0.3,0.7],[1,1]]")]
    // Endpoints not anchored at x=0 and x=1.
    [InlineData("[[0.1,0],[1,1]]")]
    // y outside the unit range.
    [InlineData("[[0,0],[0.5,1.5],[1,1]]")]
    // Non-monotone (falling) y.
    [InlineData("[[0,0],[0.5,0.8],[1,0.4]]")]
    // Top control point below 1: un-round-trippable (deadzone continuity cliff).
    [InlineData("[[0,0],[1,0.6]]")]
    // Malformed control point (three elements).
    [InlineData("[[0,0,0],[1,1]]")]
    public void Parse_rejects_an_invalid_curve_as_corrupt(string curveJson)
        => ProfileStore.Parse(DocWithCurve(curveJson)).Status.Should().Be(ParseStatus.Corrupt);

    [Fact]
    public void Save_refuses_to_overwrite_a_newer_schema_file()
    {
        var newer = "{\"version\": 999, \"payload\": null}";
        File.WriteAllText(Path_, newer);
        var store = new ProfileStore(new ProfileStoreOptions(_dir));

        var act = () => store.Save(Make("racing"));

        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path_).Should().Be(newer, "the newer file must not be clobbered");
        File.Exists(Path_ + ".bak.1").Should().BeFalse("no backup generation should be consumed");
    }

    [Fact]
    public void Save_over_a_corrupt_primary_quarantines_it_instead_of_rotating_it_into_backups()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 5));
        store.Save(Make("racing"));
        store.Save(Make("racing"));
        store.Save(Make("racing")); // bak.1 and bak.2 both hold good generations
        var goodBak1 = File.ReadAllText(Path_ + ".bak.1");
        File.WriteAllText(Path_, "{ corrupt bytes");

        store.Save(Make("racing"));

        File.ReadAllText(Path_ + ".corrupt").Should().Be("{ corrupt bytes", "the corrupt bytes must be preserved as evidence");
        File.ReadAllText(Path_ + ".bak.1").Should().Be(goodBak1, "a corrupt primary must not consume a good backup generation");
        Store_Parses(Path_).Should().BeTrue("the new content must be written as the primary");
    }

    private static bool Store_Parses(string path)
        => ProfileStore.Parse(File.ReadAllText(path)).Status == ParseStatus.Ok;
}
