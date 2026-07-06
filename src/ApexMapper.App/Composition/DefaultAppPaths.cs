using System.IO;

namespace ApexMapper.App.Composition;

/// <summary>
/// Production implementation of <see cref="IAppPaths"/>.
/// All paths are resolved relative to <c>%AppData%\ApexMapper</c>.
/// </summary>
public sealed class DefaultAppPaths : IAppPaths
{
    private static readonly string s_appDataRoot =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ApexMapper");

    public string ProfilesDirectory => Path.Combine(s_appDataRoot, "profiles");

    public string DeviceRegistryFile => Path.Combine(s_appDataRoot, "device-registry.json");

    public string PanicPolicyDirectory => s_appDataRoot;

    public string ProfilePinDirectory => s_appDataRoot;

    public string LogDirectory => Path.Combine(s_appDataRoot, "logs");

    public string ExecutablePath =>
        Environment.ProcessPath
        ?? System.Reflection.Assembly.GetEntryAssembly()?.Location
        ?? throw new InvalidOperationException("Cannot determine the current process executable path.");
}
