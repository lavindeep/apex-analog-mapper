using System.ComponentModel;
using ApexMapper.App.Model;
using ApexMapper.App.Mvvm;
using ApexMapper.App.Storage;
using Velopack;

namespace ApexMapper.App.ViewModels;

/// <summary>
/// The setup card's updates (U1, E7). At launch it asks GitHub for a newer version unless
/// it had an answer in the last six hours, which the settings keep, or the user turned the
/// launch check off, or the settings that would say so could not be read; a launch check
/// that fails is only logged. Test versions are offered while this copy is one, or when
/// the user asks for them. Nothing downloads until the user presses Update, and the new
/// version installs on a restart the user starts, never while mapping.
/// </summary>
public sealed class UpdatesViewModel : ObservableObject
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private readonly AppServices _services;
    private readonly Workspace _workspace;
    private readonly SemanticVersion _current;
    private bool _prereleases;
    private bool _checkOnLaunch;
    private Step _step;
    private bool _checked;
    private string? _found;
    private int _percent;
    private string? _problem;

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
        _prereleases = settings.Prereleases;
        _checkOnLaunch = settings.CheckForUpdates;
        Act = new Command(OnAct, () => _step switch
        {
            Step.Idle when _found is null => true,
            Step.Idle or Step.Ready => !_workspace.SessionActive,
            _ => false,
        });
        workspace.PropertyChanged += OnWorkspaceChanged;
        if (!services.Updates.Installed || !_checkOnLaunch || workspace.SettingsUnread)
        {
            return;
        }
        var now = services.UtcNow();
        if (settings.UpdateCheckedAt is { } at && at <= now && now - at < CacheFor)
        {
            _checked = true;
            _found = Newer(settings.UpdateFound);
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
        Step.Ready => $"Version {_found} is downloaded. Restart the app to install it, or it installs the next time the app starts."
            + (_workspace.SessionActive ? " Stop mapping first." : ""),
        Step.Installing => $"Closing to install version {_found}.",
        _ when !_services.Updates.Installed => "This copy was not installed with Setup.exe, so it cannot update itself.",
        _ when _found is not null => $"Version {_found} is available." + (_workspace.SessionActive ? " Stop mapping to update." : ""),
        _ when _checked => "This is the newest version.",
        _ => null,
    };

    /// <summary>What a screen reader announces: the text without the percent, which changes all through a download.</summary>
    public string? Spoken => _step is Step.Downloading ? $"Downloading version {_found}." : Text;

    /// <summary>The button's label, or null when it is hidden.</summary>
    public string? ActText => _step switch
    {
        Step.Ready => "Restart and update",
        Step.Idle when _services.Updates.Installed => _found is null ? "Check for updates" : "Update",
        _ => null,
    };

    /// <summary>Checks, downloads, or restarts to install, depending on how far the update got.</summary>
    public Command Act { get; }

    public bool Installed => _services.Updates.Installed;

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
    public bool CanChoosePrereleases => _services.Updates.Installed && !_current.IsPrerelease;

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
                _problem = "Could not reach GitHub to look for a newer version: " + e.Message;
            }
        }
        Move(Step.Idle);
    }

    private async Task DownloadAsync()
    {
        _problem = null;
        _percent = 0;
        Move(Step.Downloading);
        try
        {
            _found = Newer(await _services.Updates.DownloadAsync(OfferPrereleases, OnProgress));
            Move(_found is null ? Step.Idle : Step.Ready);
        }
        catch (Exception e)
        {
            _services.Log("Update download failed: " + e);
            _problem = "The download failed: " + e.Message;
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
        if (_workspace.SessionActive)
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
            _problem = "The update could not start: " + e.Message;
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
        Raise(nameof(Text));
        Raise(nameof(Spoken));
        Raise(nameof(ActText));
        Raise(nameof(Available));
        Act.Refresh();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.SessionActive))
        {
            Raise(nameof(Text));
            Raise(nameof(Spoken));
            Act.Refresh();
        }
    }
}
