using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using ApexMapper.Persistence.Profiles;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Profiles;

public class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-store-" + Guid.NewGuid().ToString("N"));

    public ProfileStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Profile SampleProfile() => new(
        Id: "racing",
        Name: "Racing",
        Device: new DeviceMatcher(0x1038, 0x161C, null, null),
        Game: new GameMatcher(null, null, null),
        Activation: ActivationPolicy.Default,
        SingleBindings: new[]
        {
            new SingleKeyBinding(KeyId.FromScanCode(0x11), BindingTarget.RightTrigger, LinearCurve.Instance, 120f, 0f),
        },
        AxisBindings: new[]
        {
            new AxisPairBinding(
                KeyId.FromScanCode(0x1E),
                KeyId.FromScanCode(0x20),
                BindingTarget.LeftStickX,
                LinearCurve.Instance,
                80f, 80f,
                SocdMode.Neutral),
        },
        Notes: null);

    [Fact]
    public void Save_then_LoadAll_round_trips_profile()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir));
        store.Save(SampleProfile());
        var loaded = store.LoadAll();
        loaded.Should().ContainSingle();
        loaded[0].Id.Should().Be("racing");
        loaded[0].SingleBindings.Should().HaveCount(1);
        loaded[0].AxisBindings.Should().HaveCount(1);
    }

    [Fact]
    public void Save_then_LoadAll_round_trips_a_per_binding_curve_and_deadzone()
    {
        var cubic = new PiecewiseCubicCurve(new[] { (0f, 0f), (0.5f, 0.3f), (1f, 1f) });
        var shaped = new DeadzoneCurve(cubic, innerDeadzone: 0.1f, outerDeadzone: 0.9f);
        var profile = SampleProfile() with
        {
            SingleBindings = new[]
            {
                new SingleKeyBinding(KeyId.FromScanCode(0x11), BindingTarget.RightTrigger, shaped, 120f, 0f),
            },
        };

        var store = new ProfileStore(new ProfileStoreOptions(_dir));
        store.Save(profile);
        var loaded = store.LoadAll().Single();

        var curve = loaded.SingleBindings[0].Curve.Should().BeOfType<DeadzoneCurve>().Subject;
        curve.InnerDeadzone.Should().BeApproximately(0.1f, 1e-6f);
        curve.OuterDeadzone.Should().BeApproximately(0.9f, 1e-6f);
        curve.Inner.Should().BeOfType<PiecewiseCubicCurve>();
        foreach (var x in new[] { 0.05f, 0.2f, 0.5f, 0.8f, 0.95f })
        {
            curve.Map(x).Should().BeApproximately(shaped.Map(x), 1e-5f);
        }
    }

    [Fact]
    public void Save_never_produces_a_file_its_own_LoadAll_quarantines()
    {
        // A curve that survives construction must survive its own round-trip: Save must never
        // emit a file the next LoadAll would classify corrupt and quarantine.
        var cubic = new PiecewiseCubicCurve(new[] { (0f, 0f), (0.5f, 0.3f), (1f, 1f) });
        var profile = SampleProfile() with
        {
            SingleBindings = new[]
            {
                new SingleKeyBinding(KeyId.FromScanCode(0x11), BindingTarget.RightTrigger, cubic, 0f, 0f),
            },
        };
        var store = new ProfileStore(new ProfileStoreOptions(_dir));
        store.Save(profile);

        store.LoadAll(out var reports);

        reports.Should().BeEmpty();
    }

    [Fact]
    public void Save_writes_one_file_per_profile()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir));
        store.Save(SampleProfile());
        var files = Directory.GetFiles(_dir, "*.json");
        files.Should().ContainSingle();
        Path.GetFileName(files[0]).Should().Be("racing.json");
    }

    [Fact]
    public void Load_recovers_with_defaults_when_a_file_is_corrupt()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        var store = new ProfileStore(new ProfileStoreOptions(_dir));
        var loaded = store.LoadAll();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_skips_unknown_future_versions()
    {
        File.WriteAllText(Path.Combine(_dir, "future.json"), "{\"version\": 999, \"payload\": null}");
        var store = new ProfileStore(new ProfileStoreOptions(_dir));
        store.LoadAll().Should().BeEmpty();
    }

    private const string LegacyV1Json = """
        {
          "version": 1,
          "payload": {
            "id": "legacy",
            "name": "Legacy",
            "device": { "vendor_id": 4152, "product_id": 5660, "serial_number": null, "product_name_pattern": null },
            "game": { "executable_name": null, "window_title_pattern": null, "steam_app_id": null },
            "activation": { "scope": "foreground_only", "focus_loss_debounce_ms": 500, "auto_enable": false, "requires_opt_in_for_protected_games": true },
            "single_bindings": [
              { "source": { "scan_code": 17 }, "target": "right_trigger", "curve": null, "press_ramp_ms": 120, "release_ramp_ms": 0 }
            ],
            "axis_bindings": [],
            "notes": null
          }
        }
        """;

    [Fact]
    public void LoadAll_migrates_a_v1_document_to_a_usable_profile()
    {
        File.WriteAllText(Path.Combine(_dir, "legacy.json"), LegacyV1Json);
        var store = new ProfileStore(new ProfileStoreOptions(_dir));

        var loaded = store.LoadAll();

        loaded.Should().ContainSingle();
        loaded[0].Id.Should().Be("legacy");
        loaded[0].SingleBindings.Should().ContainSingle(b => b.Target == BindingTarget.RightTrigger);
    }

    [Fact]
    public void LoadAll_migrates_lazily_and_leaves_the_v1_file_untouched_on_disk()
    {
        var path = Path.Combine(_dir, "legacy.json");
        File.WriteAllText(path, LegacyV1Json);
        var store = new ProfileStore(new ProfileStoreOptions(_dir));

        store.LoadAll();

        // Migration is applied in memory only; the on-disk file is rewritten to the current
        // schema on the next Save, not during load.
        File.ReadAllText(path).Should().Contain("\"version\": 1");
    }

    [Fact]
    public void Saving_a_loaded_v1_profile_stamps_the_current_schema_and_reloads_clean()
    {
        var path = Path.Combine(_dir, "legacy.json");
        File.WriteAllText(path, LegacyV1Json);
        var store = new ProfileStore(new ProfileStoreOptions(_dir));

        var loaded = store.LoadAll().Single();
        store.Save(loaded);

        File.ReadAllText(path).Should().Contain("\"version\": 2");
        store.LoadAll(out var reports);
        reports.Should().BeEmpty("the rewritten profile is at the current schema and loads cleanly");
    }

    [Fact]
    public void LoadAll_migrates_a_v1_document_containing_a_line_comment()
    {
        // The store's serializer tolerates comments, so a v1 file with one must migrate and load
        // rather than throwing out of the migration step and taking every profile down with it.
        var withComment = LegacyV1Json.Replace(
            "\"version\": 1,",
            "\"version\": 1, // legacy profile\n");
        File.WriteAllText(Path.Combine(_dir, "legacy.json"), withComment);
        var store = new ProfileStore(new ProfileStoreOptions(_dir));

        var loaded = store.LoadAll();

        loaded.Should().ContainSingle();
        loaded[0].Id.Should().Be("legacy");
    }

    [Fact]
    public void LoadAll_migrates_a_v1_document_containing_a_trailing_comma()
    {
        var withTrailingComma = LegacyV1Json.Replace(
            "\"notes\": null\n",
            "\"notes\": null,\n");
        File.WriteAllText(Path.Combine(_dir, "legacy.json"), withTrailingComma);
        var store = new ProfileStore(new ProfileStoreOptions(_dir));

        var loaded = store.LoadAll();

        loaded.Should().ContainSingle();
        loaded[0].Id.Should().Be("legacy");
    }
}
