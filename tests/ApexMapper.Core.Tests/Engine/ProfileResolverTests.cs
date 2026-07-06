using ApexMapper.Core;
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

    private static ForegroundContext Ctx(string executablePath, string windowTitle, string? steamAppId = null) =>
        new(executablePath, windowTitle, 0u, steamAppId, System.DateTimeOffset.MinValue);

    [Fact]
    public void Manual_pin_overrides_everything()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var exact = MakeProfile("exact", new GameMatcher("forza.exe", null, null));
        var pinned = MakeProfile("pinned", new GameMatcher(null, null, null));
        var resolver = new ProfileResolver(new[] { generic, exact, pinned });

        var ctx = Ctx("forza.exe", "Forza");
        resolver.Resolve(ctx, manualPinId: "pinned").Should().Be(pinned);
    }

    [Fact]
    public void Exact_executable_beats_window_title_and_generic()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var titled = MakeProfile("titled", new GameMatcher(null, "Forza.*", null));
        var exe = MakeProfile("exe", new GameMatcher("forza.exe", null, null));
        var resolver = new ProfileResolver(new[] { generic, titled, exe });

        var ctx = Ctx("forza.exe", "Forza Horizon 5");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(exe);
    }

    [Fact]
    public void Exact_executable_matches_on_bare_file_name_of_a_full_path()
    {
        // The foreground exe arrives as a full path; the matcher holds only the
        // bare (lower-case) file name, so resolution must extract the file name.
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var exe = MakeProfile("exe", new GameMatcher("forza.exe", null, null));
        var resolver = new ProfileResolver(new[] { generic, exe });

        var fullPath = System.IO.Path.Combine("Games", "Sub Dir", "FORZA.EXE");
        var ctx = Ctx(fullPath, "Some Window");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(exe);
    }

    [Fact]
    public void Window_title_beats_generic()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var titled = MakeProfile("titled", new GameMatcher(null, "Forza Horizon 5", null));
        var resolver = new ProfileResolver(new[] { generic, titled });

        var ctx = Ctx("game.exe", "Forza Horizon 5");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(titled);
    }

    [Fact]
    public void Window_title_matches_case_insensitively()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var titled = MakeProfile("titled", new GameMatcher(null, "Forza Horizon 5", null));
        var resolver = new ProfileResolver(new[] { generic, titled });

        var ctx = Ctx("game.exe", "forza horizon 5");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(titled);
    }

    [Fact]
    public void Window_title_requires_exact_equality_not_substring()
    {
        // "Forza" would have matched the old unanchored regex against this title; exact
        // equality no longer treats it as a match.
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var titled = MakeProfile("titled", new GameMatcher(null, "Forza", null));
        var resolver = new ProfileResolver(new[] { generic, titled });

        var ctx = Ctx("game.exe", "Forza Horizon 5");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(generic);
    }

    [Fact]
    public void Malformed_window_title_string_never_throws()
    {
        // An unbalanced-bracket string was a fatal regex before; it is now just a title that
        // does not equal the foreground title, and Resolve completes without throwing.
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var titled = MakeProfile("titled", new GameMatcher(null, "[unclosed(group", null));
        var resolver = new ProfileResolver(new[] { generic, titled });

        var ctx = Ctx("game.exe", "Some Window");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(generic);
    }

    [Fact]
    public void Returns_generic_when_nothing_matches()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var resolver = new ProfileResolver(new[] { generic });

        var ctx = Ctx("random.exe", "Random Window");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(generic);
    }

    [Fact]
    public void Returns_null_when_no_profile_matches()
    {
        var resolver = new ProfileResolver(Array.Empty<Profile>());
        var ctx = Ctx("random.exe", "Random");
        resolver.Resolve(ctx, manualPinId: null).Should().BeNull();
    }

    [Fact]
    public void Steam_app_id_match_counts_as_exact_executable()
    {
        var generic = MakeProfile("generic", new GameMatcher(null, null, null));
        var steam = MakeProfile("steam", new GameMatcher(null, null, "1551360"));
        var resolver = new ProfileResolver(new[] { generic, steam });

        var ctx = Ctx("ForzaHorizon5.exe", "Forza Horizon 5", "1551360");
        resolver.Resolve(ctx, manualPinId: null).Should().Be(steam);
    }
}
