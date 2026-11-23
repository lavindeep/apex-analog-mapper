using System.ComponentModel;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.App.Storage;
using Velopack;

namespace ApexMapper.App.ViewModels;

/// <summary>
/// The Settings page's updates, and Home's update row (U1, E7). At launch it asks GitHub for a newer version unless
/// it had an answer in the last six hours, which the settings keep, or the user turned the
/// launch check off, or the settings that would say so could not be read; a launch check
/// that fails is only logged. Test versions are offered while this copy is one, or when
/// the user asks for them. Nothing downloads until the user presses Update, and the new
/// version installs on a restart the user starts, never while mapping. A CI build never
/// looks.
/// </summary>
public sealed class UpdatesViewModel : ObservableObject
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private readonly SemanticVersion _current;

    /// <summary>
    /// CI packs every build as 0.1.0-ci.&lt;run&gt; (ci.yml). Its portable zip reads as installed
    /// and older than every release, so it would offer the latest release in place of the
    /// build under test.
    /// </summary>
    private readonly bool _ciBuild;
    private bool _prereleases;
    private bool _checkOnLaunch;
    private Step _step;
    private bool _checked;
    private bool _offered;
    private string? _found;
    private int _percent;
    private string? _problem;
    private bool _downloadFailed;

    private enum Step
    {
        Idle,
        Checking,
        Downloading,
        Ready,
        Installing,
    }

    public UpdatesViewModel(AppServices services, Workspace workspace, AppSettings settings)
    {
        _services = services;
        _workspace = workspace;
        _current = SemanticVersion.Parse(services.AppVersion);
        _ciBuild = _current.ReleaseLabels.FirstOrDefault() == "ci";
        _prereleases = settings.Prereleases;
        _checkOnLaunch = settings.CheckForUpdates;
        Act = new Command(OnAct, () => _step switch
        {
            Step.Idle when _found is null => true,
            Step.Idle => !_workspace.SessionActive,
            Step.Ready => !_workspace.SessionActive && !_workspace.ProfileUnsaved,
            _ => false,
        });
        workspace.PropertyChanged += OnWorkspaceChanged;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Text))
            {
                Raise(nameof(Notice));
                Raise(nameof(NoticeSpoken));
            }
        };
        if (!CanUpdate || !_checkOnLaunch || workspace.SettingsUnread)
        {
            return;
        }
        var now = services.UtcNow();
        if (settings.UpdateCheckedAt is { } at && at <= now && now - at < CacheFor)
        {
            _checked = true;
            _found = Newer(settings.UpdateFound);
            _offered = _found is not null;
        }
        else
        {
            Background.Run(CheckAsync(byUser: false));
        }
    }

    public string VersionText => $"Apex Analog Mapper {_services.AppVersion}";

    /// <summary>A newer version the user can download, or null.</summary>
    public string? Available => _step is Step.Idle ? _found : null;

    public string? Text => _problem ?? _step switch
    {
        Step.Checking => "Looking for a newer version.",
        Step.Downloading => $"Downloading version {_found}: {_percent}%.",
        Step.Ready => $"Version {_found} is downloaded. "
            + (_workspace.SessionActive ? (_workspace.ProfileUnsaved ? "Stop mapping and save or discard your profile changes, then press" : "Stop mapping, then press")
                : _workspace.ProfileUnsaved ? "Save or discard your profile changes, then press" : "Press")
            + " Restart and update to install it now, or it installs the next time the app starts.",
        Step.Installing => $"Closing to install version {_found}.",
        _ when _ciBuild => "This is a test build from CI, so it does not look for updates.",
        _ when !_services.Updates.Installed => "This copy was not installed with Setup.exe, so it cannot update itself. To get updates, install the app with Setup.exe from its GitHub releases page.",
        _ when _found is not null && _downloadFailed => "The download failed." + (_workspace.SessionActive ? " Stop mapping, then press Update to try again."
            : " Press Update to try again."),
        _ when _found is not null => $"Version {_found} is available." + (_workspace.SessionActive ? " Stop mapping to update."
            : " Update downloads it, and it installs when you restart the app."),
        _ when _checked => "This is the newest version.",
        _ => null,
    };

    /// <summary>What a screen reader announces: the text without the percent, which changes all through a download.</summary>
    public string? Spoken => _step is Step.Downloading ? $"Downloading version {_found}." : Text;

    /// <summary>Home's line from the first time a newer version is offered, so what happens to it later shows there too; else null.</summary>
    public string? Notice => CanUpdate && _offered ? Text : null;

    public string? NoticeSpoken => Notice is null ? null : Spoken;

    /// <summary>The button's label, or null when it is hidden.</summary>
    public string? ActText => _step switch
    {
        Step.Ready => "Restart and update",
        Step.Idle when CanUpdate => _found is null ? "Check for updates" : "Update",
        _ => null,
    };

    /// <summary>Checks, downloads, or restarts to install, depending on how far the update got.</summary>
    public Command Act { get; }

    /// <summary>An installed copy that is not a CI build, which may look for updates and install them.</summary>
    public bool CanUpdate => _services.Updates.Installed && !_ciBuild;

    /// <summary>Look for a newer version when the app starts. Off, the app only looks when asked.</summary>
    public bool CheckOnLaunch
    {
        get => _checkOnLaunch;
        set
        {
            if (Set(ref _checkOnLaunch, value))
            {
                _workspace.Remember(_services.Settings, s => s with { CheckForUpdates = value });
            }
        }
    }

    /// <summary>Test versions are always offered while this copy is one, so the switch only shows on a release.</summary>
    public bool CanChoosePrereleases => CanUpdate && !_current.IsPrerelease;

    /// <summary>Offer test versions too. Changing it looks again at once.</summary>
    public bool Prereleases
    {
        get => _prereleases;
        set
        {
            if (Set(ref _prereleases, value))
            {
                _workspace.Remember(_services.Settings, s => s with { Prereleases = value });
                if (_step is Step.Idle)
                {
                    _found = null;
                    Background.Run(CheckAsync(byUser: true));
                }
            }
        }
    }

    private bool OfferPrereleases => _prereleases || _current.IsPrerelease;

    private void OnAct()
    {
        switch (_step)
        {
            case Step.Idle when _found is null:
                Background.Run(CheckAsync(byUser: true));
                break;
            case Step.Idle:
                Background.Run(DownloadAsync());
                break;
            case Step.Ready:
                InstallAndRestart();
                break;
        }
    }

    private async Task CheckAsync(bool byUser)
    {
        _problem = null;
        _downloadFailed = false;
        Move(Step.Checking);
        try
        {
            // When the test versions switch moves while GitHub answers, the answer is for the old setting.
            string? found;
            bool asked;
            do
            {
                asked = OfferPrereleases;
                found = await _services.Updates.FindAsync(asked);
            }
            while (asked != OfferPrereleases);
            _found = Newer(found);
            _checked = true;
            _workspace.Remember(_services.Settings, s => s with { UpdateCheckedAt = _services.UtcNow(), UpdateFound = found }, byUser: false);
        }
        catch (Exception e)
        {
            _services.Log("Update check failed: " + e.Message);
            if (byUser)
            {
                _problem = "Could not reach GitHub to look for a newer version. Check the connection and press Check for updates again.";
            }
        }
        Move(Step.Idle);
    }

    private async Task DownloadAsync()
    {
        _problem = null;
        _downloadFailed = false;
        _percent = 0;
        Move(Step.Downloading);
        try
        {
            _found = Newer(await _services.Updates.DownloadAsync(OfferPrereleases, OnProgress));
            if (_found is null)
            {
                _problem = "That version is no longer available. This is the newest version.";
            }
            Move(_found is null ? Step.Idle : Step.Ready);
        }
        catch (Exception e)
        {
            _services.Log("Update download failed: " + e);
            _downloadFailed = true;
            Move(Step.Idle);
        }
    }

    private void OnProgress(int percent) => _services.Post(() =>
    {
        if (_step is Step.Downloading)
        {
            _percent = percent;
            Raise(nameof(Text));
        }
    });

    private void InstallAndRestart()
    {
        if (_workspace.SessionActive || _workspace.ProfileUnsaved)
        {
            return;
        }
        try
        {
            _services.Updates.InstallOnExit();
        }
        catch (Exception e)
        {
            _services.Log("Starting the updater failed: " + e);
            _problem = "The update could not start. Close the app and open it again to install it. The log says why it failed.";
            Raise(nameof(Text));
            Raise(nameof(Spoken));
            return;
        }
        _services.Log($"Closing to install version {_found}.");
        // The updater is on its way, so a second press must not start another.
        _problem = null;
        Move(Step.Installing);
        _services.Close();
    }

    /// <summary>The version when it is newer than this copy, else null.</summary>
    private string? Newer(string? version) =>
        version is not null && SemanticVersion.TryParse(version, out var parsed) && parsed > _current ? version : null;

    private void Move(Step step)
    {
        _step = step;
        _offered |= _found is not null;
        Raise(nameof(Text));
        Raise(nameof(Spoken));
        Raise(nameof(ActText));
        Raise(nameof(Available));
        Act.Refresh();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Workspace.SessionActive) or nameof(Workspace.ProfileUnsaved))
        {
            Raise(nameof(Text));
            Raise(nameof(Spoken));
            Act.Refresh();
        }
    }
}
