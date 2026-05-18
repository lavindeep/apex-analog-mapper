using ApexMapper.Core.Engine;
using ApexMapper.Core.Pipeline;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Engine;

public class ProfileResolverTests
{
    private static Profile MakeProfile(string id, GameMatcher game) => new(
        Id: id,
        Name: id,
        Device: new DeviceMatcher(0x1038, 0x161C, null, null),
        Game: game,
        Activation: ActivationPolicy.Default,
        SingleBindings: Array.Empty<SingleKeyBinding>(),
        AxisBindings: Array.Empty<AxisPairBinding>(),
        Notes: null);

    [Fact]
    public void Manual_pin_overrides_everything()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var exact = MakeProfile("exact", new GameMatcher("forza.exe", null, null));
        var pinned = MakeProfile("pinned", new GameMatcher(null, null, null));
        var resolver = new ProfileResolver(new[] { generic, exact, pinned });

        var ctx = new ForegroundContext("forza.exe", "Forza", null);
        resolver.Resolve(ctx, manualPinId: "pinned").Should().Be(pinned);
    }

    [Fact]
    public void Exact_executable_beats_window_title_and_generic()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var titled = MakeProfile("titled", new GameMatcher(null, "Forza.*", null));
        var exe = MakeProfile("exe", new GameMatcher("forza.exe", null, null));
        var resolver = new ProfileResolver(new[] { generic, titled, exe });

        var ctx = new ForegroundContext("forza.exe", "Forza Horizon 5", null);
        resolver.Resolve(ctx, manualPinId: null).Should().Be(exe);
    }

    [Fact]
    public void Window_title_beats_generic()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var titled = MakeProfile("titled", new GameMatcher(null, "Forza.*", null));
        var resolver = new ProfileResolver(new[] { generic, titled });

        var ctx = new ForegroundContext("game.exe", "Forza Horizon 5", null);
        resolver.Resolve(ctx, manualPinId: null).Should().Be(titled);
    }

    [Fact]
    public void Returns_generic_when_nothing_matches()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var resolver = new ProfileResolver(new[] { generic });

        var ctx = new ForegroundContext("random.exe", "Random Window", null);
        resolver.Resolve(ctx, manualPinId: null).Should().Be(generic);
    }

    [Fact]
    public void Returns_null_when_no_profile_matches()
    {
        var resolver = new ProfileResolver(Array.Empty<Profile>());
        var ctx = new ForegroundContext("random.exe", "Random", null);
        resolver.Resolve(ctx, manualPinId: null).Should().BeNull();
    }

    [Fact]
    public void Steam_app_id_match_counts_as_exact_executable()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var steam = MakeProfile("steam", new GameMatcher(null, null, "1551360"));
        var resolver = new ProfileResolver(new[] { generic, steam });

        var ctx = new ForegroundContext("ForzaHorizon5.exe", "Forza Horizon 5", "1551360");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(steam);
    }
}
