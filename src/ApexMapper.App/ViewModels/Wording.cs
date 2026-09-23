using ApexMapper.Windows.Input;

namespace ApexMapper.App.ViewModels;

/// <summary>Sentence pieces the cards share.</summary>
internal static class Wording
{
    /// <summary>"W", "W and S", "W, S and D".</summary>
    public static string List(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>B10: why the game's keys may not reach the app, and what to do, or null when they do.</summary>
    public static string? RunAsAdministrator(Elevation elevation, string game) => elevation switch
    {
        Elevation.Elevated => $"This app cannot see the keys of {game}, which runs as administrator. Close this app, then right-click it and choose Run as administrator.",
        Elevation.Unknown => $"Windows would not say whether {game} runs as administrator. If its keys do not reach it, close this app, then right-click it and choose Run as administrator.",
        _ => null,
    };
}
