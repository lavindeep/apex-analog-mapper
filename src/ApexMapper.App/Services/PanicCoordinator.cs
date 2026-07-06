namespace ApexMapper.App.Services;

/// <summary>
/// Orchestrates the panic flow: registers a global hotkey, captures the current
/// foreground executable, disables its auto-enable policy, and submits the panic
/// to the supervisor channel.
/// </summary>
public sealed class PanicCoordinator
{
    private const string HotkeyId = "panic";

    private readonly IHotkeyService _hotkeyService;
    private readonly ISupervisorChannel _supervisor;
    private readonly IForegroundWatcher _foregroundWatcher;
    private readonly IPanicPolicyStore _policyStore;

    public PanicCoordinator(
        IHotkeyService hotkeyService,
        ISupervisorChannel supervisor,
        IForegroundWatcher foregroundWatcher,
        IPanicPolicyStore policyStore)
    {
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _foregroundWatcher = foregroundWatcher ?? throw new ArgumentNullException(nameof(foregroundWatcher));
        _policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
    }

    /// <summary>Raised after <see cref="PanicAsync"/> completes (success or failure).</summary>
    public event EventHandler<PanicCompletedEventArgs>? PanicCompleted;

    /// <summary>Registers the panic hotkey with the given gesture.</summary>
    public void Start(HotkeyGesture gesture)
    {
        _hotkeyService.Register(HotkeyId, gesture, OnHotkeyPressed);
    }

    /// <summary>Unregisters the panic hotkey.</summary>
    public void Stop()
    {
        _hotkeyService.Unregister(HotkeyId);
    }

    /// <summary>
    /// Performs the panic sequence: best-effort disables auto-enable for the current
    /// foreground exe (if non-empty), then always submits panic to the supervisor.
    /// Exceptions from the policy write and from the supervisor are each caught and
    /// routed through <see cref="PanicCompleted"/> — a failing policy store must never
    /// block the panic frame (fail-closed: zeroing output is the safety-critical step).
    /// </summary>
    public async Task PanicAsync(CancellationToken ct)
    {
        var exe = _foregroundWatcher.Current.ExecutablePath;

        // The policy write is best-effort: a throwing store (disk full, ACL, AV lock)
        // must not prevent the panic frame from reaching the supervisor. Capture the
        // failure and surface it for diagnostics, but carry on regardless.
        Exception? policyError = null;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                _policyStore.DisableAutoEnable(exe);
            }
            catch (Exception ex)
            {
                policyError = ex;
            }
        }

        Exception? error = null;
        try
        {
            await _supervisor.SubmitPanicAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        PanicCompleted?.Invoke(this, new PanicCompletedEventArgs(exe, error, policyError));
    }

    // Called on the WPF UI dispatcher by NHotkey. Dispatch onto a background task
    // so the dispatcher is never blocked and exceptions never escape into it.
    private void OnHotkeyPressed()
    {
        _ = Task.Run(() => PanicAsync(CancellationToken.None));
    }
}
