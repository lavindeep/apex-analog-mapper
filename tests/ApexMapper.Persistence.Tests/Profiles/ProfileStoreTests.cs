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
}
