using Velopack;
using Velopack.Sources;

namespace ApexMapper.App.Update;

/// <summary>
/// Where new versions come from. The app asks the project's GitHub Releases through
/// Velopack; view model tests use a fake, so they never reach the network.
/// </summary>
public interface IUpdates
{
    /// <summary>False when the app runs from a build folder instead of an install, which cannot update itself.</summary>
    bool Installed { get; }

    /// <summary>The newest version on offer when it is newer than this one, or null. Throws when the releases cannot be read.</summary>
    Task<string?> FindAsync(bool prereleases);

    /// <summary>
    /// Looks again and downloads the newest version, reporting percent done from any
    /// thread. Returns that version, or null when there is nothing newer after all.
    /// </summary>
    Task<string?> DownloadAsync(bool prereleases, Action<int> progress);

    /// <summary>Starts Velopack's updater, which waits for the app to exit, installs the download and starts the new version.</summary>
    void InstallOnExit();
}

/// <summary>
/// <see cref="IUpdates"/> over Velopack, which needs <c>VelopackApp.Run</c> to have run
/// first. Requests go to GitHub unsigned, which allows 60 an hour from one address; the
/// six-hour cache in the settings stays far below that.
/// </summary>
public sealed class VelopackUpdates : IUpdates
{
    public const string Repository = "https://github.com/lavindeep/apex-analog-mapper";

    private (UpdateManager Manager, VelopackAsset Release)? _downloaded;

    public bool Installed { get; } = IsInstalled();

    public async Task<string?> FindAsync(bool prereleases)
    {
        var manager = Manager(prereleases);
        var found = await Task.Run(manager.CheckForUpdatesAsync);
        return found?.TargetFullRelease.Version.ToString();
    }

    public async Task<string?> DownloadAsync(bool prereleases, Action<int> progress)
    {
        var manager = Manager(prereleases);
        if (await Task.Run(manager.CheckForUpdatesAsync) is not { } found)
        {
            return null;
        }
        await Task.Run(() => manager.DownloadUpdatesAsync(found, progress));
        _downloaded = (manager, found.TargetFullRelease);
        return found.TargetFullRelease.Version.ToString();
    }

    public void InstallOnExit()
    {
        var (manager, release) = _downloaded ?? throw new InvalidOperationException("No update has been downloaded.");
        manager.WaitExitThenApplyUpdates(release, silent: false, restart: true);
    }

    /// <summary>
    /// Starts Velopack's updater for a version downloaded in an earlier run, which installs
    /// it once this copy exits and starts the new version. False when there is none, or
    /// when the updater cannot start and the app should run as it is.
    /// </summary>
    public static bool InstallPendingOnExit()
    {
        try
        {
            var manager = Manager(prereleases: false);
            if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } pending)
            {
                return false;
            }
            manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A copy whose install Velopack cannot read cannot update either, and the app still starts.</summary>
    private static bool IsInstalled()
    {
        try
        {
            return Manager(prereleases: false).IsInstalled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static UpdateManager Manager(bool prereleases) => new(new GithubSource(Repository, accessToken: null, prereleases));
}
