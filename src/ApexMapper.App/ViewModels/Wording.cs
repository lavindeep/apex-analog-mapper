using ApexMapper.Windows.Input;

namespace ApexMapper.App.ViewModels;

/// <summary>Sentence pieces the cards share.</summary>
internal static class Wording
{
    /// <summary>"W", "W and S", "W, S and D".</summary>
    public static string List(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>B10: why the app leaves the game alone, and what to do, or null when it maps the game.</summary>
    public static string? RunAsAdministrator(Elevation elevation, string game) => elevation switch
    {
        Elevation.Elevated => $"Because {game} runs as administrator and this app does not, this app leaves it alone. " + LeftAlone,
        Elevation.Unknown => $"Windows would not say whether {game} runs as administrator, so this app leaves it alone. " + LeftAlone,
        _ => null,
    };

    private const string LeftAlone = "Its keys reach it as plain key presses and the controller stays at rest. " +
        "To map it, close this app, then right-click it and choose Run as administrator.";
}
