using ApexMapper.Core.Engine;
using ApexMapper.Persistence.Profiles;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="IProfileHotReload"/>.
/// Watches the profiles directory with a <see cref="FileSystemWatcher"/> filtered
/// to <c>*.json</c>. On any Created/Changed/Renamed event it debounces for
/// <see cref="ProfileHotReloadOptions.DebounceDelay"/> (default 200 ms), then
/// calls <see cref="ProfileStore.LoadAll"/> and raises <see cref="ProfilesReloaded"/>.
/// Errors during load are swallowed and logged — never thrown out of the event.
/// </summary>
public sealed class ProfileHotReload : IProfileHotReload
{
    private readonly ProfileStore                _store;
    private readonly ProfileHotReloadOptions     _options;
    private readonly ILogger<ProfileHotReload>   _logger;
    private readonly TimeProvider                _time;
    private readonly object                      _lock = new();

    private FileSystemWatcher? _watcher;
    private ITimer?            _debounceTimer;
    private bool               _started;
    private bool               _disposed;

    public event EventHandler<ProfilesReloadedEventArgs>? ProfilesReloaded;

    public ProfileHotReload(
        ProfileStore store,
        ProfileHotReloadOptions options,
        ILogger<ProfileHotReload> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store   = store;
        _options = options;
        _logger  = logger;
        _time    = time ?? TimeProvider.System;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;

            Directory.CreateDirectory(_options.DirectoryPath);

            var watcher = new FileSystemWatcher(_options.DirectoryPath, "*.json")
            {
                NotifyFilter            = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents     = true,
                IncludeSubdirectories   = false,
            };

            watcher.Created += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Renamed += OnFileRenamed;

            _watcher = watcher;
        }
    }

    public void Stop()
    {
        FileSystemWatcher? watcher;
        ITimer? timer;

        lock (_lock)
        {
            if (!_started) return;
            _started = false;

            watcher        = _watcher;
            _watcher       = null;
            timer          = _debounceTimer;
            _debounceTimer = null;
        }

        timer?.Dispose();

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFileEvent;
            watcher.Changed -= OnFileEvent;
            watcher.Renamed -= OnFileRenamed;
            watcher.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
    }

    // -----------------------------------------------------------------------
    // Internal testing seam
    // -----------------------------------------------------------------------

    /// <summary>
    /// Directly invokes the debounce callback — allows tests to trigger the
    /// reload without relying on real <see cref="FileSystemWatcher"/> events.
    /// </summary>
    internal void TriggerNowForTesting() => CommitDebounced(null);

    // -----------------------------------------------------------------------
    // Private
    // -----------------------------------------------------------------------

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleDebounce();

    private void OnFileRenamed(object sender, RenamedEventArgs e) => ScheduleDebounce();

    private void ScheduleDebounce()
    {
        ITimer? oldTimer;
        lock (_lock)
        {
            if (!_started) return;

            oldTimer       = _debounceTimer;
            _debounceTimer = _time.CreateTimer(
                CommitDebounced,
                state:  null,
                dueTime: _options.DebounceDelay,
                period:  Timeout.InfiniteTimeSpan);
        }

        oldTimer?.Dispose();
    }

    private void CommitDebounced(object? state)
    {
        lock (_lock)
        {
            if (!_started) return;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        IReadOnlyList<Profile> profiles;
        try
        {
            profiles = _store.LoadAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProfileHotReload: failed to load profiles from {Directory}", _options.DirectoryPath);
            return;
        }

        try
        {
            ProfilesReloaded?.Invoke(this, new ProfilesReloadedEventArgs(profiles));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProfileHotReload: unhandled exception in ProfilesReloaded handler");
        }
    }
}
