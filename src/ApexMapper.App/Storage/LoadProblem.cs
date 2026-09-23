using System.IO;
using ApexMapper.Core.Storage;

namespace ApexMapper.App.Storage;

/// <summary>The one sentence the window shows about a file that did not load cleanly, and the rule for saving over it.</summary>
internal static class LoadProblem
{
    /// <summary>Null when the file loaded or did not exist.</summary>
    public static string? Describe<T>(LoadResult<T> result, string what) => result.Status switch
    {
        LoadStatus.Recovered when result.Error is null => $"The {what} file was missing or damaged, so its backup was used.",
        LoadStatus.Recovered => $"The {what} file was missing or damaged, so its backup was used. {result.Error}",
        LoadStatus.Corrupt => $"The {what} file could not be read. {result.Error}",
        LoadStatus.Unavailable => $"The {what} file could not be opened: {result.Error}",
        LoadStatus.Newer => $"The {what} file was written by a newer version of the app, so this version leaves it alone and cannot save changes to it. Update the app to use it, or move the file and its .bak copy out of the folder to start over.",
        _ => null,
    };

    /// <summary>
    /// A file that exists but could not be read, or that a newer version wrote, must not
    /// be written over: what the app holds in its place is a default, not the user's data.
    /// </summary>
    public static void ThrowIfUnsafeToSave<T>(LoadResult<T> result, string what)
    {
        if (result.Status is LoadStatus.Unavailable or LoadStatus.Newer)
        {
            throw new IOException(Describe(result, what));
        }
    }
}
