using ApexMapper.Core.Storage;

namespace ApexMapper.App.Storage;

/// <summary>The one sentence the window shows about a file that did not load cleanly.</summary>
internal static class LoadProblem
{
    /// <summary>Null when the file loaded or did not exist.</summary>
    public static string? Describe<T>(LoadResult<T> result, string what) => result.Status switch
    {
        LoadStatus.Recovered => $"The {what} file was damaged and was restored from its backup.",
        LoadStatus.Corrupt => $"The {what} file could not be read and was kept aside as a .corrupt file. {result.Error}",
        LoadStatus.Unavailable => $"The {what} file could not be opened: {result.Error}",
        _ => null,
    };
}
