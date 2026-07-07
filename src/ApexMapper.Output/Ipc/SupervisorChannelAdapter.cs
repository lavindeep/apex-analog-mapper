using ApexMapper.Core.Pipeline;

namespace ApexMapper.Output.Ipc;

/// <summary>
/// Resilient tray-side channel over <see cref="SupervisorClient"/>: owns the
/// send cadence and the connection lifecycle the raw client deliberately does
/// not. The mapping loop writes the current pad state into a latest-wins slot
/// (<see cref="SetState"/>, also exposed as <see cref="IPadStateSink"/>);
/// while a session is live the adapter submits that state every control
/// interval and a heartbeat every heartbeat interval, driven by the injected
/// <see cref="TimeProvider"/>.
///
/// The client's ConnectAsync is single-owner (not safe against concurrent
/// calls), so all connecting funnels through one serialized driver: a
/// compare-and-swap admits exactly one connect loop at a time, and each
/// session gets a fresh client from the factory. Between sessions submits are
/// simply skipped — the supervisor's heartbeat gap zeroes the pad on its own,
/// which is the designed fail-closed backstop while the tray is away.
///
/// A send failure never throws out of a timer callback: the failed send
/// faults the client, the client raises Disconnected, and the adapter tears
/// the session down and reconnects with doubling, capped backoff.
///
/// <see cref="StatusChanged"/> transitions are stamped inside the state lock
/// and raised in stamp order, exactly once per transition, under a dedicated
/// raise gate. No adapter state lock is held while handlers run: paths take
/// the raise gate after releasing the state lock and never the reverse, so a
/// handler may call back into the adapter without deadlocking.
/// </summary>
public sealed class SupervisorChannelAdapter : IPadStateSink, IAsyncDisposable
{
    private readonly Func<SupervisorClient> _clientFactory;
    private readonly SupervisorChannelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly object _statusGate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly CancellationToken _lifetimeToken;

    private SupervisorClient? _client;
    private ITimer? _controlTimer;
    private ITimer? _heartbeatTimer;
    private VirtualPadState _state;
    private bool _autoReconnect;
    private long _statusSeq;
    private long _lastRaisedSeq;
    private bool _statusConnected;
    private int _driverRunning;
    private int _controlInFlight;
    private int _heartbeatInFlight;
    private int _disposed;

    public SupervisorChannelAdapter(
        string sessionId,
        SupervisorChannelOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(CreateDefaultFactory(sessionId, timeProvider), options, timeProvider)
    {
    }

    // Test seam: lets a suite substitute the client (and through it the transport).
    internal SupervisorChannelAdapter(
        Func<SupervisorClient> clientFactory,
        SupervisorChannelOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _options = options ?? new SupervisorChannelOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Captured once: touching _lifetimeCts.Token after disposal would throw.
        _lifetimeToken = _lifetimeCts.Token;

        if (_options.ControlInterval <= TimeSpan.Zero
            || _options.HeartbeatInterval <= TimeSpan.Zero
            || _options.ReconnectInitialDelay <= TimeSpan.Zero
            || _options.ReconnectMaxDelay < _options.ReconnectInitialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Intervals must be positive and the reconnect cap must not be below the initial delay.");
        }
    }

    /// <summary>Raised once per connectivity transition, in transition order:
    /// true when a session is established, false when it ends (with the fault
    /// cause, or null for a clean or owner-initiated end).</summary>
    public event Action<bool, Exception?>? StatusChanged;

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _client is not null;
            }
        }
    }

    /// <summary>
    /// Enables the channel and returns immediately; the session is established
    /// and maintained in the background. Observe <see cref="StatusChanged"/> or
    /// <see cref="IsConnected"/> for the outcome.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        lock (_sync)
        {
            _autoReconnect = true;
        }

        TriggerDriver();
    }

    /// <summary>Latest-wins slot the mapping loop writes; the control cadence
    /// snapshots it on each send. Cheap and safe at tick rate.</summary>
    public void SetState(in VirtualPadState state)
    {
        lock (_sync)
        {
            _state = state;
        }
    }

    void IPadStateSink.Push(in VirtualPadState state) => SetState(in state);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        SupervisorClient? client;
        ITimer? control;
        ITimer? heartbeat;
        long stamp = 0;
        lock (_sync)
        {
            _autoReconnect = false;
            client = _client;
            _client = null;
            control = _controlTimer;
            heartbeat = _heartbeatTimer;
            _controlTimer = null;
            _heartbeatTimer = null;
            if (client is not null)
            {
                stamp = ++_statusSeq;
            }
        }

        control?.Dispose();
        heartbeat?.Dispose();

        if (client is not null)
        {
            // Best-effort courtesy zero so the pad rests immediately rather
            // than after the supervisor's heartbeat gap; bounded so a wedged
            // pipe cannot wedge disposal.
            await TrySubmitZeroAsync(client, "channel disposed").ConfigureAwait(false);
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            RaiseStatus(stamp, connected: false, error: null);
        }

        _lifetimeCts.Dispose();
    }

    private static Func<SupervisorClient> CreateDefaultFactory(string sessionId, TimeProvider? timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        return () => new SupervisorClient(sessionId, connectTimeout: null, timeProvider);
    }

    // Exactly one connect driver may run at a time: SupervisorClient.ConnectAsync
    // is single-owner, and a second concurrent driver could double-connect.
    private void TriggerDriver()
    {
        if (Interlocked.CompareExchange(ref _driverRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(RunDriverAsync);
    }

    private async Task RunDriverAsync()
    {
        try
        {
            await ConnectLoopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposal cancelled the connect or a backoff wait; just unwind.
        }
        finally
        {
            Volatile.Write(ref _driverRunning, 0);
            // Lost-wakeup guard: a disconnect that raced this driver's exit saw
            // the compare-and-swap still taken and could not start a new one.
            if (!_lifetimeToken.IsCancellationRequested && ShouldConnect())
            {
                TriggerDriver();
            }
        }
    }

    // Retries forever with doubling, capped backoff: the supervisor frees its
    // pipe within about a second of losing a client, so the next capped retry
    // gets back in once it is reachable. Each driver run restarts the ladder
    // at the initial delay.
    private async Task ConnectLoopAsync()
    {
        TimeSpan delay = _options.ReconnectInitialDelay;
        while (true)
        {
            if (!ShouldConnect())
            {
                return;
            }

            SupervisorClient client = _clientFactory();
            // Subscribe before connecting: a session that drops immediately after
            // the handshake must not slip between connect and subscription.
            client.Disconnected += error => OnClientDisconnected(client, error);
            try
            {
                await client.ConnectAsync(_lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await DisposeQuietlyAsync(client).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await DisposeQuietlyAsync(client).ConfigureAwait(false);
                await Task.Delay(delay, _timeProvider, _lifetimeToken).ConfigureAwait(false);
                delay = NextDelay(delay);
                continue;
            }

            long stamp = 0;
            bool published;
            lock (_sync)
            {
                // The IsConnected check closes the pre-publish drop race: if the
                // session died before we got here, its Disconnected handler either
                // already ran (and found nothing to tear down) or will run and find
                // this client published — both paths end with the session down.
                published = _autoReconnect
                    && Volatile.Read(ref _disposed) == 0
                    && _client is null
                    && client.IsConnected;
                if (published)
                {
                    _client = client;
                    _controlTimer = _timeProvider.CreateTimer(OnControlTimer, null, _options.ControlInterval, Timeout.InfiniteTimeSpan);
                    _heartbeatTimer = _timeProvider.CreateTimer(OnHeartbeatTimer, null, _options.HeartbeatInterval, Timeout.InfiniteTimeSpan);
                    stamp = ++_statusSeq;
                }
            }

            if (!published)
            {
                await DisposeQuietlyAsync(client).ConfigureAwait(false);
                if (!ShouldConnect())
                {
                    return;
                }

                // Connected but dropped before publish: back off like a failure
                // so an accept-then-die supervisor cannot spin the driver hot.
                await Task.Delay(delay, _timeProvider, _lifetimeToken).ConfigureAwait(false);
                delay = NextDelay(delay);
                continue;
            }

            RaiseStatus(stamp, connected: true, error: null);
            return;
        }
    }

    private TimeSpan NextDelay(TimeSpan delay)
    {
        TimeSpan doubled = delay + delay;
        return doubled > _options.ReconnectMaxDelay ? _options.ReconnectMaxDelay : doubled;
    }

    private bool ShouldConnect()
    {
        lock (_sync)
        {
            return _autoReconnect && Volatile.Read(ref _disposed) == 0 && _client is null;
        }
    }

    private void OnClientDisconnected(SupervisorClient client, Exception? error)
    {
        ITimer? control;
        ITimer? heartbeat;
        long stamp;
        lock (_sync)
        {
            if (!ReferenceEquals(_client, client))
            {
                return; // stale session, or a client that was never published
            }

            _client = null;
            control = _controlTimer;
            heartbeat = _heartbeatTimer;
            _controlTimer = null;
            _heartbeatTimer = null;
            stamp = ++_statusSeq;
        }

        control?.Dispose();
        heartbeat?.Dispose();
        _ = DisposeQuietlyAsync(client);
        RaiseStatus(stamp, connected: false, error);

        if (ShouldConnect())
        {
            TriggerDriver();
        }
    }

    private void OnControlTimer(object? state)
    {
        SupervisorClient? client;
        ITimer? timer;
        lock (_sync)
        {
            client = _client;
            timer = _controlTimer;
        }

        if (timer is null)
        {
            return; // session torn down between fire and callback
        }

        // Skip when a previous send is still in flight: the next tick sends the
        // then-latest state instead of queuing a stale one behind a slow pipe.
        if (client is not null && Interlocked.CompareExchange(ref _controlInFlight, 1, 0) == 0)
        {
            _ = SendControlAsync(client);
        }

        Rearm(timer, _options.ControlInterval);
    }

    private void OnHeartbeatTimer(object? state)
    {
        SupervisorClient? client;
        ITimer? timer;
        lock (_sync)
        {
            client = _client;
            timer = _heartbeatTimer;
        }

        if (timer is null)
        {
            return;
        }

        if (client is not null && Interlocked.CompareExchange(ref _heartbeatInFlight, 1, 0) == 0)
        {
            _ = SendHeartbeatAsync(client);
        }

        Rearm(timer, _options.HeartbeatInterval);
    }

    private async Task SendControlAsync(SupervisorClient client)
    {
        try
        {
            VirtualPadState snapshot;
            lock (_sync)
            {
                snapshot = _state;
            }

            await client.SubmitControlAsync(PadStatePayload.From(in snapshot), _lifetimeToken).ConfigureAwait(false);
        }
        catch
        {
            // Contained: a faulted send already raised Disconnected on the
            // client, which drives the teardown; nothing may escape the cadence.
        }
        finally
        {
            Volatile.Write(ref _controlInFlight, 0);
        }
    }

    private async Task SendHeartbeatAsync(SupervisorClient client)
    {
        try
        {
            await client.SubmitHeartbeatAsync(_lifetimeToken).ConfigureAwait(false);
        }
        catch
        {
            // Contained; see SendControlAsync.
        }
        finally
        {
            Volatile.Write(ref _heartbeatInFlight, 0);
        }
    }

    private static void Rearm(ITimer timer, TimeSpan interval)
    {
        try
        {
            timer.Change(interval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Torn down between fire and re-arm.
        }
    }

    private void RaiseStatus(long stamp, bool connected, Exception? error)
    {
        lock (_statusGate)
        {
            // Stamps are allocated inside the state lock, so raising in stamp
            // order preserves transition order even when the raising threads
            // race; a superseded raise is dropped rather than delivered late.
            if (stamp <= _lastRaisedSeq)
            {
                return;
            }

            _lastRaisedSeq = stamp;
            if (_statusConnected == connected)
            {
                return;
            }

            _statusConnected = connected;
            StatusChanged?.Invoke(connected, error);
        }
    }

    private static async Task TrySubmitZeroAsync(SupervisorClient client, string reason)
    {
        try
        {
            await client.SubmitZeroAsync(reason, CancellationToken.None)
                .WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the supervisor's heartbeat gap is the backstop.
        }
    }

    private static async Task DisposeQuietlyAsync(SupervisorClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort teardown; a dispose failure here is not actionable.
        }
    }
}
