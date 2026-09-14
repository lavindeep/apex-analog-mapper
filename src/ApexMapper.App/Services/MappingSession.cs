using System.Diagnostics;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Output.Detection;
using ApexMapper.Output.Preflight;
using Microsoft.Extensions.Logging;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="IMappingSession"/>. Enable order is deliberate:
/// pre-flight and anti-cheat run before anything is started (fail-closed
/// gate), the supervisor is spawned before the channel connects (so the
/// connect's retry loop has something to reach), and the engine is enabled
/// last. Disable order is the mirror image with safety first: engine off and
/// store gated before the channel is asked to zero+disconnect, so a wedged
/// channel can never delay the local off.
///
/// There is no auto-enable path anywhere in the app today — every enable is a
/// user action. The anti-cheat verdict therefore gates the manual path via an
/// explicit confirmation prompt; if an auto-enable feature is ever added it
/// must consult the same detector and skip enabling entirely on a
/// DisableAutoEnable verdict.
/// </summary>
public sealed class MappingSession : IMappingSession, IDisposable
{
    private readonly KeyStateStore _store;
    private readonly MappingEngine _engine;
    private readonly ISupervisorChannel _channel;
    private readonly PreflightRunner _preflight;
    private readonly AntiCheatDetector _antiCheat;
    private readonly SteamDetector _steam;
    private readonly ISupervisorProcessLauncher _launcher;
    private readonly IForegroundWatcher _foreground;
    private readonly Func<string, string, bool> _confirm;
    private readonly ILogger<MappingSession> _logger;
    private readonly Func<string?> _inputReadiness;
    private readonly IGameSelection? _gameSelection;
    private readonly IKeyboardSuppression? _keyboardSuppression;
    private readonly Func<IReadOnlyCollection<KeyId>> _suppressionKeys;
    private readonly Func<IReadOnlyCollection<KeyId>> _mappedKeys;
    private IDisposable? _suppressionLease;
    private CancellationTokenSource? _suppressionStartupCancellation;
    private int _inputEditors;
    private int _starting;
    private int _channelArmed;
    private int _disposed;
    private readonly SemaphoreSlim _transition = new(1, 1);

    private volatile bool _enabled;
    private volatile string? _inputStartupBlocker = "Input pipeline is still starting.";

    // Bumped by every ForceLocalOff (panic) BEFORE it writes any state. An
    // in-flight EnableAsync snapshots this at entry and re-reads it after it has
    // armed the engine; a mismatch means a panic interleaved (worst case: while
    // the enable was parked on the unbounded anti-cheat confirm dialog), so the
    // enable unwinds instead of leaving live output post-panic. Panic keeps
    // last-word authority: the increment-then-write in ForceLocalOff pairs with
    // the arm-then-recheck here so one of the two orderings always ends off.
    private int _panicGeneration;

    /// <param name="confirm">
    /// Blocking user confirmation (title, message) → proceed?. Production wires
    /// <see cref="IDialogService.Confirm"/>; tests inject a recorder.
    /// </param>
    public MappingSession(
        KeyStateStore store,
        MappingEngine engine,
        ISupervisorChannel channel,
        PreflightRunner preflight,
        AntiCheatDetector antiCheat,
        SteamDetector steam,
        ISupervisorProcessLauncher launcher,
        IForegroundWatcher foreground,
        Func<string, string, bool> confirm,
        ILogger<MappingSession> logger,
        Func<string?>? inputReadiness = null,
        IGameSelection? gameSelection = null,
        IKeyboardSuppression? keyboardSuppression = null,
        Func<IReadOnlyCollection<KeyId>>? suppressionKeys = null,
        Func<IReadOnlyCollection<KeyId>>? mappedKeys = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _antiCheat = antiCheat ?? throw new ArgumentNullException(nameof(antiCheat));
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inputReadiness = inputReadiness ?? (() => null);
        _gameSelection = gameSelection;
        _keyboardSuppression = keyboardSuppression;
        _suppressionKeys = suppressionKeys ?? (() => Array.Empty<KeyId>());
        _mappedKeys = mappedKeys ?? (() => Array.Empty<KeyId>());
        _channel.StatusChanged += OnChannelStatusChanged;
        if (_gameSelection is not null) _gameSelection.Changed += OnGameChanged;
        if (_keyboardSuppression is not null) _keyboardSuppression.Faulted += OnSuppressionFaulted;
    }

    public bool IsEnabled => _enabled;

    internal IDisposable BlockForInputEditing()
    {
        Interlocked.Increment(ref _inputEditors);
        ForceLocalOff("Input editor opened.");
        return new InputEditLease(this);
    }

    private string? InputReadiness() => Volatile.Read(ref _inputEditors) > 0
        ? "Close the editor before starting." : _inputReadiness();

    private sealed class InputEditLease(MappingSession owner) : IDisposable
    {
        private MappingSession? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } session)
                Interlocked.Decrement(ref session._inputEditors);
        }
    }

    public event EventHandler<MappingSessionStateChangedEventArgs>? StateChanged;

    /// <summary>Called once after input and the mapping loop start, or startup fails.</summary>
    internal void CompleteInputStartup(string? error = null)
    {
        _inputStartupBlocker = error is null ? null : $"Input pipeline failed to start: {error}";
        RaiseState(false, _inputStartupBlocker);
    }

    public async Task<bool> EnableAsync(CancellationToken ct)
    {
        // Snapshot the panic generation BEFORE waiting for the transition lock:
        // an enable can queue here behind a slow disable, and a panic pressed
        // while it is queued must still unwind this enable. Snapshotting after
        // the wait would absorb that panic into the baseline and arm anyway.
        // Any panic from this point on bumps the generation, and the recheck
        // after the arm below unwinds.
        var panicGenerationAtEntry = Volatile.Read(ref _panicGeneration);

        await _transition.WaitAsync(ct).ConfigureAwait(false);
        var connectionAttempted = false;
        var completed = false;
        CancellationTokenSource? suppressionStartup = null;
        try
        {
            if (_enabled)
            {
                return true;
            }

            Volatile.Write(ref _starting, 1);
            using var cancellation = ct.Register(() => ForceLocalOff("Start cancelled."));
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0
                || Volatile.Read(ref _panicGeneration) != panicGenerationAtEntry)
                return false;

            if (_inputStartupBlocker is { } inputBlocker)
            {
                RaiseState(false, $"Cannot enable: {inputBlocker}");
                return false;
            }

            GameProcess? selectedGame = null;
            var gameError = _gameSelection?.ValidateSelectedGame(out selectedGame);
            if (gameError is not null || (_keyboardSuppression is not null && selectedGame is null))
            {
                RaiseState(false, gameError ?? "Choose a running game before starting.");
                return false;
            }

            if (InputReadiness() is { } readinessError)
            {
                RaiseState(false, readinessError);
                return false;
            }

            if (_keyboardSuppression is not null && _mappedKeys().Any(MappingKeyRules.IsReserved))
            {
                RaiseState(false, MappingKeyRules.ReservedKeyError);
                return false;
            }

            // 1. Pre-flight: any Fail issue is a blocker — output stays off.
            var report = _preflight.RunAll();
            if (report.HasBlocker)
            {
                var blocker = report.Issues.First(i => i.Severity == PreflightSeverity.Fail);
                var message = blocker.Remediation is null
                    ? blocker.Message
                    : $"{blocker.Message} {blocker.Remediation}";
                _logger.LogWarning("Enable blocked by pre-flight ({CheckId}): {Message}", blocker.CheckId, blocker.Message);
                RaiseState(false, $"Cannot enable: {message}");
                return false;
            }

            var foreground = ToDetectionContext(_foreground.Current);

            // 2. Anti-cheat: detect and disable, never evade. This is a manual
            // enable, so a positive (or unattestable) scan demands explicit
            // consent instead of silently proceeding.
            var verdict = _antiCheat.Evaluate(foreground);
            if (verdict.Action == AntiCheatAction.DisableAutoEnable)
            {
                var reason = verdict.Reason ?? "An anti-cheat signal was detected.";
                if (!_confirm("Apex Analog Mapper", $"{reason}\n\nEnable the mapper anyway?"))
                {
                    _logger.LogInformation("Enable declined by the user after anti-cheat verdict: {Reason}", reason);
                    RaiseState(false, null);
                    return false;
                }

                _logger.LogWarning("User confirmed enable despite anti-cheat verdict: {Reason}", reason);
            }

            // 3. Steam: advisory only — warn, never block.
            string? warning = EvaluateSteamAdvisory(foreground);

            // 4. Supervisor process: no supervisor, no pad — fail closed.
            if (_launcher.EnsureRunning() is { } launchError)
            {
                _logger.LogError("Enable failed: {Error}", launchError);
                RaiseState(false, launchError);
                return false;
            }

            // ConnectAsync starts a background connection attempt. Wait for an
            // actual ready channel before suppressing any physical key.
            connectionAttempted = true;
            await _channel.ConnectAsync(ct).ConfigureAwait(false);
            var connectedAt = Stopwatch.GetTimestamp();
            while (!_channel.IsConnected)
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _panicGeneration) != panicGenerationAtEntry)
                    return false;
                if (Stopwatch.GetElapsedTime(connectedAt) >= TimeSpan.FromSeconds(5))
                {
                    RaiseState(false, "Controller did not connect. Start again.");
                    return false;
                }
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
            Volatile.Write(ref _channelArmed, 1);
            if (ActivationError(panicGenerationAtEntry, selectedGame) is { } activationError)
            {
                RaiseState(false, activationError);
                return false;
            }

            if (_keyboardSuppression is not null)
            {
                var analogKeys = _suppressionKeys().ToArray();
                var digitalKeys = _mappedKeys().Except(analogKeys).ToArray();
                suppressionStartup = new CancellationTokenSource();
                var stoppingToken = suppressionStartup.Token;
                Volatile.Write(ref _suppressionStartupCancellation, suppressionStartup);
                // Stop may precede publication; otherwise it cancels this token
                // immediately, including while native Enable has not returned.
                if (Interlocked.CompareExchange(ref _panicGeneration, 0, 0) != panicGenerationAtEntry)
                    return false;
                // Native startup is bounded but must not block the UI's Stop.
                // Always observe the result, even after cancellation, so its
                // lease can be released by the generation check below.
                var lease = await Task.Run(() => _keyboardSuppression.Enable(selectedGame!.ProcessId, analogKeys, digitalKeys, stoppingToken))
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _suppressionLease, lease)?.Dispose();
            }

            ct.ThrowIfCancellationRequested();
            if (ActivationError(panicGenerationAtEntry, selectedGame) is { } errorBeforeArming)
            {
                RaiseState(false, errorBeforeArming);
                return false;
            }

            // Every Off->On transition ignores currently-held mapped keys until
            // they release once: a key first pressed while OFF and still down at
            // enable must not map instantly. Gate immediately before arming.
            _store.GateHeldKeys();
            _engine.SetEnabled(true);
            _enabled = true;

            // Panic race guard: a ForceLocalOff that interleaved anywhere above
            // (typically while parked on the confirm dialog, or while this
            // enable was queued on the transition lock) already zeroed the
            // engine/store, but the arm just above would leave live output on.
            // Detect it and unwind so panic keeps last-word authority. The read
            // goes through a full fence: the arm's release-stores could
            // otherwise reorder past a plain load on x64, letting a panic in
            // the arm-to-recheck window slip the check.
            ct.ThrowIfCancellationRequested();
            if (ActivationError(panicGenerationAtEntry, selectedGame) is { } errorAfterArming)
            {
                _logger.LogWarning("Enable cancelled: input changed or Stop was pressed.");
                RaiseState(false, errorAfterArming);
                return false;
            }

            _logger.LogInformation("Mapping enabled.");
            RaiseState(true, warning);
            completed = true;
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RaiseState(false, "Start cancelled. Start again.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StopLocalOutput();
            // Contract: EnableAsync never throws — a failed enable leaves the
            // session disabled with the failure surfaced.
            _logger.LogError(ex, "Enable failed unexpectedly.");
            RaiseState(false, $"Enable failed: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (connectionAttempted && !completed)
                {
                    StopLocalOutput();
                    await DisconnectAfterFailedStartAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _suppressionStartupCancellation, null, suppressionStartup);
                suppressionStartup?.Dispose();
                Volatile.Write(ref _starting, 0);
                _transition.Release();
            }
        }
    }

    public async Task DisableAsync(CancellationToken ct)
    {
        var hadWork = _enabled || Volatile.Read(ref _starting) != 0;
        // Stop cannot queue behind a native filter startup or a slow connection.
        StopLocalOutput();
        if (!hadWork) return;
        RaiseState(false, null);

        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StopLocalOutput();
            if (!_channel.IsConnected) return;
            try
            {
                await _channel.DisconnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Local off already happened; the supervisor's heartbeat gap
                // zeroes the pad regardless. Surface, don't fail the disable.
                _logger.LogWarning(ex, "Channel disconnect failed during disable; the supervisor's liveness gap covers the zero.");
            }

            _logger.LogInformation("Mapping disabled.");
        }
        finally
        {
            _transition.Release();
        }
    }

    public void ForceLocalOff(string reason)
    {
        // Panic path: must complete instantly. No transition lock — a concurrent
        // EnableAsync may interleave, but the panic-generation bump below (taken
        // BEFORE any state write) makes that enable unwind rather than re-arm the
        // engine, so the session always ends off, never latched-on or
        // connected-and-live. The caller owns the panic frame; this is the local
        // half only.
        StopLocalOutput();

        try
        {
            _logger.LogWarning("Local mapping forced off ({Reason}).", reason);
            RaiseState(false, null);
        }
        catch
        {
            // The safety writes above already completed; neither a throwing
            // logging provider nor a throwing subscriber may break the panic path.
        }
    }

    private void StopLocalOutput()
    {
        Interlocked.Increment(ref _panicGeneration);
        Volatile.Write(ref _channelArmed, 0);
        _engine.SetEnabled(false);
        _store.GateHeldKeys();
        _enabled = false;
        // The native worker observes this before a pending Enable returns its
        // lease. Enable owns disposal once its startup work has completed.
        var startup = Interlocked.Exchange(ref _suppressionStartupCancellation, null);
        try { startup?.Cancel(); }
        catch { /* The published lease is still released below. */ }
        // Exchange before disposing gives Stop, faults and startup cancellation
        // one owner. Native disposal disables filtering before the worker retires.
        var lease = Interlocked.Exchange(ref _suppressionLease, null);
        try { lease?.Dispose(); }
        catch { /* Local output must stay off even if filter cleanup fails. */ }
    }

    private string? ActivationError(int generation, GameProcess? expectedGame)
    {
        if (InputReadiness() is { } inputError) return inputError;
        if (_gameSelection is not null)
        {
            if (_gameSelection.ValidateSelectedGame(out var current) is { } gameError) return gameError;
            if (expectedGame?.IsSameProcess(current) != true) return "Game changed. Start again.";
        }
        if (!_channel.IsConnected) return "Controller disconnected. Start again.";
        return Interlocked.CompareExchange(ref _panicGeneration, 0, 0) != generation
            || Volatile.Read(ref _disposed) != 0 ? "Start cancelled. Start again." : null;
    }

    private async Task DisconnectAfterFailedStartAsync()
    {
        try { await _channel.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Channel disconnect failed after a cancelled start."); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ForceLocalOff("Application closing.");
        _channel.StatusChanged -= OnChannelStatusChanged;
        if (_gameSelection is not null) _gameSelection.Changed -= OnGameChanged;
        if (_keyboardSuppression is not null) _keyboardSuppression.Faulted -= OnSuppressionFaulted;
    }

    public void OnSystemResumed()
    {
        // Sleep/resume is an Off↔On-class transition for held keys: a key-up can
        // be missed while the machine is suspended, leaving a stale non-zero depth
        // that the 100 ms control cadence would keep transmitting as a latched
        // axis until the key is pressed and released again. Gate held keys so each
        // needs one release first. Output is deliberately left as-is — this is not
        // a panic; if mapping is enabled it stays enabled, and the gates persist
        // into the next enable if it is off.
        _store.GateHeldKeys();
        try
        {
            _logger.LogInformation("System resumed; held keys gated until released once.");
        }
        catch
        {
            // The gate write above already completed; a throwing logging provider
            // must not break the documented never-throws contract.
        }
    }

    private string? EvaluateSteamAdvisory(ForegroundContext? foreground)
    {
        if (foreground is null)
        {
            return null;
        }

        try
        {
            var verdict = _steam.Evaluate(foreground);
            if (verdict.IsSteamLaunched)
            {
                _logger.LogInformation("Steam advisory: {Reason}", verdict.Reason);
                return "The foreground game looks Steam-launched. If it does not react to the virtual pad, check Steam Input settings.";
            }
        }
        catch (Exception ex)
        {
            // Advisory only — a failed scan must not affect the enable.
            _logger.LogDebug(ex, "Steam advisory scan failed; ignoring.");
        }

        return null;
    }

    private static ForegroundContext? ToDetectionContext(ApexMapper.Core.ForegroundContext current)
    {
        if (current.ProcessId == 0 && current.ExecutablePath.Length == 0)
        {
            return null;
        }

        return new ForegroundContext(
            ProcessId: unchecked((int)current.ProcessId),
            ExecutablePath: current.ExecutablePath.Length == 0 ? null : current.ExecutablePath,
            WindowTitle: current.WindowTitle.Length == 0 ? null : current.WindowTitle,
            SteamAppId: current.SteamAppId,
            CapturedAt: current.ObservedAt);
    }

    private void OnChannelStatusChanged(object? sender, SupervisorStatusEventArgs e)
    {
        if (e.IsConnected || (!_enabled && Volatile.Read(ref _channelArmed) == 0)) return;
        StopLocalOutput();
        RaiseState(false, "Controller disconnected. Start again.");
    }

    private void OnGameChanged(object? sender, EventArgs e) => ForceLocalOff("Game changed.");

    private void OnSuppressionFaulted(string reason)
    {
        StopLocalOutput();
        RaiseState(false, reason);
    }

    private void RaiseState(bool enabled, string? message)
        => StateChanged?.Invoke(this, new MappingSessionStateChangedEventArgs(enabled, message));
}
