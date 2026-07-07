using ApexMapper.Core.Engine;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Services;

/// <summary>
/// Connects the profile sources to the mapping engine: resolves the active
/// profile from the current foreground context and the manual pin, applies it
/// to the engine (an atomic next-tick swap), and re-resolves whenever the
/// foreground changes, the profiles directory hot-reloads, or the pin changes
/// (via <see cref="Reevaluate"/>).
///
/// A same-profile re-resolution is NOT re-applied — <c>SetProfile</c> rebuilds
/// the pipeline and restarts ramps from rest, so alt-tabbing away and back must
/// not yank a held ramp to zero. A hot reload always re-applies (same id, new
/// bindings). A failed profile load applies the empty set — the engine maps
/// nothing rather than something stale (fail-safe).
/// </summary>
public sealed class ProfileActivationService : IDisposable
{
    private readonly Func<IReadOnlyList<Profile>> _loadProfiles;
    private readonly IProfileHotReload _hotReload;
    private readonly IForegroundWatcher _foreground;
    private readonly IProfileManualPinStore _pinStore;
    private readonly Action<Profile?> _applyProfile;
    private readonly ILogger<ProfileActivationService> _logger;
    private readonly object _lock = new();

    private ProfileResolver _resolver = new(Array.Empty<Profile>());
    private string? _currentProfileId;
    private bool _hasApplied;
    private bool _started;
    private bool _disposed;

    /// <param name="applyProfile">Receives the newly resolved profile (or null
    /// for none); production passes the engine's <c>SetProfile</c>.</param>
    public ProfileActivationService(
        Func<IReadOnlyList<Profile>> loadProfiles,
        IProfileHotReload hotReload,
        IForegroundWatcher foreground,
        IProfileManualPinStore pinStore,
        Action<Profile?> applyProfile,
        ILogger<ProfileActivationService> logger)
    {
        _loadProfiles = loadProfiles ?? throw new ArgumentNullException(nameof(loadProfiles));
        _hotReload = hotReload ?? throw new ArgumentNullException(nameof(hotReload));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _pinStore = pinStore ?? throw new ArgumentNullException(nameof(pinStore));
        _applyProfile = applyProfile ?? throw new ArgumentNullException(nameof(applyProfile));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The id of the currently applied profile, or null when none resolves.</summary>
    public string? CurrentProfileId
    {
        get
        {
            lock (_lock)
            {
                return _currentProfileId;
            }
        }
    }

    /// <summary>Raised when the applied profile's id changes.</summary>
    public event EventHandler? ActiveProfileChanged;

    /// <summary>Relayed after a hot reload has been applied, so UI lists can refresh.</summary>
    public event EventHandler? ProfilesReloaded;

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
        }

        IReadOnlyList<Profile> profiles;
        try
        {
            profiles = _loadProfiles();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial profile load failed; starting with no profiles.");
            profiles = Array.Empty<Profile>();
        }

        lock (_lock)
        {
            _resolver = new ProfileResolver(profiles);
        }

        _hotReload.ProfilesReloaded += OnProfilesReloaded;
        _foreground.ForegroundChanged += OnForegroundChanged;

        Reevaluate(forceApply: true);
    }

    /// <summary>Re-resolves against the current foreground and pin. Call after a
    /// pin change; foreground and reload changes re-resolve automatically.</summary>
    public void Reevaluate() => Reevaluate(forceApply: false);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _hotReload.ProfilesReloaded -= OnProfilesReloaded;
        _foreground.ForegroundChanged -= OnForegroundChanged;
    }

    private void Reevaluate(bool forceApply)
    {
        bool changed;
        lock (_lock)
        {
            if (!_started || _disposed)
            {
                return;
            }

            string? pin = null;
            try
            {
                pin = _pinStore.Get();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reading the manual pin failed; resolving without it.");
            }

            var profile = _resolver.Resolve(_foreground.Current, pin);
            var newId = profile?.Id;
            changed = !_hasApplied || !string.Equals(newId, _currentProfileId, StringComparison.Ordinal);

            if (changed || forceApply)
            {
                _applyProfile(profile);
                _hasApplied = true;
                _currentProfileId = newId;
                _logger.LogInformation("Active profile: {ProfileId}", newId ?? "(none)");
            }
        }

        if (changed)
        {
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnForegroundChanged(object? sender, ForegroundChangedEventArgs e) => Reevaluate(forceApply: false);

    private void OnProfilesReloaded(object? sender, ProfilesReloadedEventArgs e)
    {
        lock (_lock)
        {
            _resolver = new ProfileResolver(e.Profiles);
        }

        // Force: the same profile id may now carry different bindings.
        Reevaluate(forceApply: true);
        ProfilesReloaded?.Invoke(this, EventArgs.Empty);
    }
}
