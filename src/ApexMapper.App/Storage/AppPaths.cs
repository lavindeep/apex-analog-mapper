using System.IO;

namespace ApexMapper.App.Storage;

/// <summary>
/// Where the app keeps its files: one folder under the roaming app data folder. The
/// previous app's <c>ApexMapper</c> folder has a different name and is never read,
/// moved, or deleted.
/// </summary>
public sealed class AppPaths(string root)
{
    public const string FolderName = "ApexAnalogMapper";

    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName));

    public string Root { get; } = Path.GetFullPath(root);

    public string Profiles => Path.Combine(Root, "profiles");

    public string Calibration => Path.Combine(Root, "calibration");

    public string Settings => Path.Combine(Root, "settings.json");

    public string Log => Path.Combine(Root, "log.txt");
}
