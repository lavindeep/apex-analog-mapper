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

    /// <summary>Throws when Windows reports no application data folder (an offline redirected profile): the working directory is no substitute.</summary>
    public static AppPaths ForCurrentUser()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return appData.Length > 0
            ? new AppPaths(Path.Combine(appData, FolderName))
            : throw new InvalidOperationException("Windows did not report an application data folder, so there is nowhere to keep settings.");
    }

    public string Root { get; } = Path.GetFullPath(root);

    public string Profiles => Path.Combine(Root, "profiles");

    public string Calibration => Path.Combine(Root, "calibration");

    public string Settings => Path.Combine(Root, "settings.json");

    public string Log => Path.Combine(Root, "log.txt");
}
