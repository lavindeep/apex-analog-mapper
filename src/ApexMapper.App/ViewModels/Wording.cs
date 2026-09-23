namespace ApexMapper.App.ViewModels;

/// <summary>Sentence pieces the cards share.</summary>
internal static class Wording
{
    /// <summary>"W", "W and S", "W, S and D".</summary>
    public static string List(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
