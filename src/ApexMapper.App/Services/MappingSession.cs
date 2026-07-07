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
public sealed class MappingSession : IMappingSession
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
    private readonly SemaphoreSlim _transition = new(1, 1);

    private volatile bool _enabled;

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
        ILogger<MappingSession> logger)
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
    }

    public bool IsEnabled => _enabled;

    public event EventHandler<MappingSessionStateChangedEventArgs>? StateChanged;

    public async Task<bool> EnableAsync(CancellationToken ct)
    {
        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_enabled)
            {
                return true;
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
                    RaiseState(false, "Mapping stays disabled.");
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

            // 5. Channel on (background reconnect), engine last.
            await _channel.ConnectAsync(ct).ConfigureAwait(false);
            _engine.SetEnabled(true);
            _enabled = true;
            _logger.LogInformation("Mapping enabled.");
            RaiseState(true, warning ?? "Mapping enabled.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Contract: EnableAsync never throws — a failed enable leaves the
            // session disabled with the failure surfaced.
            _logger.LogError(ex, "Enable failed unexpectedly.");
            RaiseState(false, $"Enable failed: {ex.Message}");
            return false;
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task DisableAsync(CancellationToken ct)
    {
        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_enabled)
            {
                return;
            }

            // Local off first: the engine's next tick pushes a zero into the
            // channel slot, and gating means a key still held across a
            // disable/enable cycle must be released once before it maps again.
            _engine.SetEnabled(false);
            _store.GateHeldKeys();
            _enabled = false;

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
            RaiseState(false, null);
        }
        finally
        {
            _transition.Release();
        }
    }

    public void ForceLocalOff(string reason)
    {
        // Panic path: must complete instantly. No transition lock — a
        // concurrent EnableAsync may interleave, but the engine/store writes
        // here are the last-word safety writes; worst case the session ends
        // connected-but-zeroed, never with latched input. The caller owns the
        // panic frame; this is only the local half.
        _engine.SetEnabled(false);
        _store.GateHeldKeys();
        _enabled = false;
        _logger.LogWarning("Local mapping forced off ({Reason}).", reason);

        try
        {
            RaiseState(false, $"Output forced off ({reason}).");
        }
        catch
        {
            // A throwing subscriber must not break the panic path.
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

    private void RaiseState(bool enabled, string? message)
        => StateChanged?.Invoke(this, new MappingSessionStateChangedEventArgs(enabled, message));
}
