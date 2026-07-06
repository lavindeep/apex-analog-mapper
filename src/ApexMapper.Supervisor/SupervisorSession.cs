using ApexMapper.Output;
using ApexMapper.Output.Ipc;

namespace ApexMapper.Supervisor;

/// <summary>
/// Owns one connected tray client and drives one virtual pad for the duration
/// of that connection. The session is the fail-closed core of the supervisor:
/// whichever trigger ends it first — heartbeat gap, peer disconnect, connection
/// fault, panic frame, or owner shutdown — the pad is zeroed, then disconnected,
/// exactly once, and no control frame can reach the pad afterwards.
///
/// Dispatch: every known frame counts as liveness (a 100 ms control cadence
/// proves the client alive just as heartbeats do). Control frames apply their
/// payload to the pad in arrival order; sequence numbers are stamps, and
/// duplicates or reordering are not protocol violations. A control frame with
/// no payload is a protocol anomaly: it is counted and ignored — heartbeat
/// governs safety and the zero frame is the explicit zero signal. A zero frame
/// zeroes the pad and the session continues; a panic frame ends it.
///
/// Threading: frame callbacks arrive on the connection's read loop, the gap
/// fires on a timer thread, and shutdown can come from anywhere. All state
/// transitions and every pad call go through one lock — holding it across the
/// synchronous pad calls is what guarantees nothing is submitted after the
/// teardown zero. The completion signal is raised outside the lock.
/// </summary>
public sealed class SupervisorSession
{
    private readonly FrameConnection _connection;
    private readonly IControllerOutput _output;
    private readonly HeartbeatMonitor _monitor;
    private readonly object _lock = new();
    private readonly TaskCompletionSource<SessionEndReason> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connectionDisposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _nullPayloadControlFrames;
    private bool _tornDown;

    public SupervisorSession(
        FrameConnection connection,
        IControllerOutput output,
        SupervisorOptions options,
        TimeProvider? timeProvider = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        ArgumentNullException.ThrowIfNull(options);
        _monitor = new HeartbeatMonitor(timeProvider ?? TimeProvider.System, options.HeartbeatGapBeforeZero);
    }

    /// <summary>Completes with the end reason once the pad has been zeroed and
    /// disconnected; faults if the pad could not be connected at session start.</summary>
    public Task<SessionEndReason> Completion => _completion.Task;

    /// <summary>Control frames that arrived without a payload (protocol anomaly, ignored).</summary>
    public long NullPayloadControlFrames => Interlocked.Read(ref _nullPayloadControlFrames);

    /// <summary>Received frames dropped because their schema version was unknown.</summary>
    public long UnknownVersionFrames => _connection.UnknownVersionFrames;

    /// <summary>
    /// Connects the pad and pumps frames until the session ends, returning why.
    /// If the pad cannot be connected the session fails immediately — no pad,
    /// nothing to zero — and the connection is dropped so the client sees it.
    /// Cancelling <paramref name="shutdownToken"/> ends the session with
    /// <see cref="SessionEndReason.Shutdown"/>.
    /// </summary>
    public async Task<SessionEndReason> RunAsync(CancellationToken shutdownToken)
    {
        try
        {
            _output.Connect();
        }
        catch (Exception ex)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _completion.TrySetException(ex);
            throw;
        }

        _monitor.GapDetected += () => Teardown(SessionEndReason.HeartbeatGap);
        _connection.Faulted += _ => Teardown(SessionEndReason.Faulted);
        _monitor.Start();

        try
        {
            await _connection.RunReadLoopAsync(OnFrame, shutdownToken).ConfigureAwait(false);
        }
        finally
        {
            _monitor.Dispose();
        }

        // The read loop has completed. If a trigger (gap, panic, fault) already
        // tore the session down, these calls are no-ops and the first reason wins.
        if (shutdownToken.IsCancellationRequested)
        {
            Teardown(SessionEndReason.Shutdown);
        }
        else if (_connection.IsFaulted)
        {
            Teardown(SessionEndReason.Faulted);
        }
        else
        {
            Teardown(SessionEndReason.PeerDisconnected);
        }

        // Teardown always initiates the connection disposal; wait for it to
        // finish so the caller can safely rebind the pipe name afterwards.
        await _connectionDisposed.Task.ConfigureAwait(false);
        return await _completion.Task.ConfigureAwait(false);
    }

    private ValueTask OnFrame(IFrame frame)
    {
        _monitor.NotifyAlive();
        switch (frame)
        {
            case ControlFrame { Payload: null }:
                Interlocked.Increment(ref _nullPayloadControlFrames);
                break;
            case ControlFrame control:
                lock (_lock)
                {
                    if (!_tornDown)
                    {
                        // A submit failure propagates: the read loop faults the
                        // connection and the fault trigger tears the session down.
                        _output.Submit(control.Payload.ToVirtualPadState());
                    }
                }

                break;
            case HeartbeatFrame:
                break; // liveness only
            case ZeroFrame:
                lock (_lock)
                {
                    if (!_tornDown)
                    {
                        _output.Zero();
                    }
                }

                break;
            case PanicFrame:
                Teardown(SessionEndReason.Panic);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void Teardown(SessionEndReason reason)
    {
        lock (_lock)
        {
            if (_tornDown)
            {
                return;
            }

            _tornDown = true;

            // Zero before disconnect, deliberately: a connected pad must never be
            // left holding stale state, even for the instant before it detaches.
            // Each step is best-effort — a failing pad must not block the rest of
            // the teardown or crash the process.
            try
            {
                _output.Zero();
            }
            catch
            {
                // Swallowed: Disconnect below must still run.
            }

            try
            {
                _output.Disconnect();
            }
            catch
            {
                // Swallowed: the connection below must still be disposed.
            }
        }

        // Outside the lock: disposing the connection unwinds the read loop, and
        // completing the task releases waiters that may re-enter this class.
        _ = DisposeConnectionAsync();
        _completion.TrySetResult(reason);
    }

    private async Task DisposeConnectionAsync()
    {
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: transport disposal failures are not actionable here.
        }

        _connectionDisposed.TrySetResult();
    }
}
