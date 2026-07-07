using System.IO.Pipes;
using ApexMapper.Output;
using ApexMapper.Output.Ipc;

namespace ApexMapper.Supervisor;

/// <summary>
/// Accept loop over the supervisor's per-session named pipe.
///
/// Contract: strictly sequential, single-session. The server accepts one
/// client, runs one <see cref="SupervisorSession"/> to completion, disposes
/// that pipe instance, then creates a fresh instance and accepts again. There
/// are no concurrent sessions and no supersede: while a session is live the
/// pipe's only instance is taken, so a second client cannot connect. If the
/// tray dies without closing its pipe, the heartbeat gap
/// (<see cref="SupervisorOptions.HeartbeatGapBeforeZero"/>) tears the session
/// down and frees the server; a restarting tray owns its own connect retry, so
/// tray and supervisor can restart independently without a zombie pad.
///
/// The pipe is created with <see cref="PipeOptions.CurrentUserOnly"/>: only the
/// same user may connect, so a foreign local user can neither drive nor read
/// the pad channel.
///
/// The loop never dies silently: a failed session or a failed accept is
/// contained and the loop continues with a fresh instance; repeated pipe
/// creation failures back off instead of spinning hot. The loop ends two ways,
/// distinguished by <see cref="Completion"/>: <see cref="StopAsync"/> tears
/// down any active session (<see cref="SessionEndReason.Shutdown"/>: pad zeroed
/// and disconnected); or a full <see cref="SupervisorOptions.IdleExitTimeout"/>
/// window passes with no connected session and the loop retires itself
/// (<see cref="ServerExitReason.IdleTimeout"/>) instead of lingering after the
/// tray exits — the tray respawns a supervisor on the next enable. The idle
/// window never runs during a session: it is measured from start until the
/// first connection and from each session end until the next.
/// </summary>
public sealed class SupervisorServer : IAsyncDisposable
{
    private const int NotStarted = 0;
    private const int Started = 1;
    private const int Stopped = 2;

    private readonly string _sessionId;
    private readonly Func<IControllerOutput> _outputFactory;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopCts = new();

    private Task<ServerExitReason> _loop = Task.FromResult(ServerExitReason.Stopped);
    private int _state = NotStarted;
    private int _disposed;
    private long _failedSessionStarts;
    private long _pipeFailures;

    public SupervisorServer(
        string sessionId,
        Func<IControllerOutput> outputFactory,
        SupervisorOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _sessionId = sessionId;
        _outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after each session ends, with why. Not raised for a session
    /// that failed before its pad connected (see <see cref="FailedSessionStarts"/>).</summary>
    public event Action<SessionEndReason>? SessionEnded;

    /// <summary>Short human-readable lines describing loop activity: a client
    /// connecting, a session ending (with reason and per-session anomaly
    /// counters), a pre-connect pad failure (with its message), and pipe or
    /// accept failures (with their message). Raised outside any lock; a throwing
    /// subscriber is contained and never stops the loop.</summary>
    public event Action<string>? Diagnostics;

    /// <summary>Sessions that died before their pad connected (pad connect failure).</summary>
    public long FailedSessionStarts => Interlocked.Read(ref _failedSessionStarts);

    /// <summary>Failures creating or accepting on the session pipe. While these
    /// accumulate the loop is backing off instead of serving a client.</summary>
    public long PipeFailures => Interlocked.Read(ref _pipeFailures);

    /// <summary>Completes when the accept loop has fully unwound, with why it
    /// ended. Before <see cref="Start"/> it is completed as
    /// <see cref="ServerExitReason.Stopped"/>.</summary>
    public Task<ServerExitReason> Completion => _loop;

    public void Start()
    {
        int previous = Interlocked.CompareExchange(ref _state, Started, NotStarted);
        if (previous != NotStarted)
        {
            throw new InvalidOperationException(
                previous == Started ? "The server is already started." : "The server has been stopped.");
        }

        _loop = Task.Run(() => RunLoopAsync(_stopCts.Token));
    }

    /// <summary>Stops accepting, tears down any active session (pad zeroed and
    /// disconnected), and returns once the loop has fully unwound. Idempotent.</summary>
    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _state, Stopped);
        await _stopCts.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }

    private async Task<ServerExitReason> RunLoopAsync(CancellationToken stopToken)
    {
        var consecutiveFailures = 0;

        // One idle window spans everything between sessions, including pipe
        // failures and their backoff: it is armed at start, re-armed after each
        // session ends, and disposed the moment a client is accepted — so it can
        // never fire against a live session, and a session that outlives the
        // window is followed by a fresh full window, not an expired one.
        var idleWindow = new IdleWindow(_timeProvider, _options.IdleExitTimeout);
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    // Explicit buffer sizes (one max frame each way): Windows treats
                    // them as the pipe's write-quota hint, so the client's writes get
                    // a deterministic kernel buffer instead of the advisory-minimum
                    // default, which can park a writer until the reader drains.
                    pipe = new NamedPipeServerStream(
                        PipeNames.ForSession(_sessionId),
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                        inBufferSize: FrameCodec.MaxFrameBytes,
                        outBufferSize: FrameCodec.MaxFrameBytes);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _pipeFailures);
                    RaiseDiagnostics($"pipe creation failed: {ex.Message}");
                    consecutiveFailures++;
                    if (!await BackoffAsync(consecutiveFailures, stopToken).ConfigureAwait(false))
                    {
                        return ServerExitReason.Stopped;
                    }

                    if (idleWindow.HasElapsed)
                    {
                        return ServerExitReason.IdleTimeout;
                    }

                    continue;
                }

                using (var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken, idleWindow.Token))
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(acceptCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // An accepted connection wins any race with the idle
                        // deadline: cancellation can surface even though the
                        // connect completed (the platforms differ on when), and
                        // such a client must be served, never torn down. Only a
                        // cancelled wait with no accepted client ends the loop.
                        if (stopToken.IsCancellationRequested || !pipe.IsConnected)
                        {
                            await DisposeQuietlyAsync(pipe).ConfigureAwait(false);
                            return stopToken.IsCancellationRequested
                                ? ServerExitReason.Stopped
                                : ServerExitReason.IdleTimeout;
                        }
                    }
                    catch (Exception ex)
                    {
                        await DisposeQuietlyAsync(pipe).ConfigureAwait(false);
                        Interlocked.Increment(ref _pipeFailures);
                        RaiseDiagnostics($"accept failed: {ex.Message}");
                        consecutiveFailures++;
                        if (!await BackoffAsync(consecutiveFailures, stopToken).ConfigureAwait(false))
                        {
                            return ServerExitReason.Stopped;
                        }

                        if (idleWindow.HasElapsed)
                        {
                            return ServerExitReason.IdleTimeout;
                        }

                        continue;
                    }
                }

                consecutiveFailures = 0;
                idleWindow.Dispose();
                await RunOneSessionAsync(pipe, stopToken).ConfigureAwait(false);
                idleWindow = new IdleWindow(_timeProvider, _options.IdleExitTimeout);
            }

            return ServerExitReason.Stopped;
        }
        finally
        {
            idleWindow.Dispose();
        }
    }

    private async Task RunOneSessionAsync(NamedPipeServerStream pipe, CancellationToken stopToken)
    {
        RaiseDiagnostics("session started");

        var connection = new FrameConnection(pipe);
        SupervisorSession? session = null;
        SessionEndReason? reason = null;
        Exception? startFailure = null;
        try
        {
            session = new SupervisorSession(connection, _outputFactory(), _options, _timeProvider);
            reason = await session.RunAsync(stopToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Contained: a session that failed to start (pad connect failure, or
            // the output factory itself throwing) must not kill the accept loop.
            // The detail is kept so diagnostics can report why it failed.
            Interlocked.Increment(ref _failedSessionStarts);
            startFailure = ex;
        }
        finally
        {
            // The session disposes the connection on every path it owns; this
            // covers the paths it never reached (e.g. the factory threw).
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
        }

        if (startFailure is not null)
        {
            RaiseDiagnostics($"session failed to start: {startFailure.Message}");
        }
        else if (reason is { } endReason)
        {
            RaiseDiagnostics(
                $"session ended: {endReason} (unknownVersionFrames={session!.UnknownVersionFrames}, " +
                $"nullPayloadControlFrames={session.NullPayloadControlFrames})");
            try
            {
                SessionEnded?.Invoke(endReason);
            }
            catch
            {
                // Contained: a throwing subscriber must not kill the accept loop —
                // the tray could then never reconnect and the pad would stay down
                // with no way back until a restart.
            }
        }
    }

    private void RaiseDiagnostics(string message)
    {
        try
        {
            Diagnostics?.Invoke(message);
        }
        catch
        {
            // Contained: a throwing diagnostics subscriber must not kill the loop.
        }
    }

    private static async Task<bool> BackoffAsync(int consecutiveFailures, CancellationToken stopToken)
    {
        // Linear backoff capped at one second: enough to avoid spinning hot when
        // pipe creation keeps failing, short enough that a recovered environment
        // is picked up quickly.
        var delay = TimeSpan.FromMilliseconds(Math.Min(100 * consecutiveFailures, 1000));
        try
        {
            await Task.Delay(delay, stopToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: transport disposal failures are not actionable here.
        }
    }

    /// <summary>One idle window between sessions: a single-shot deadline whose
    /// token cancels the pending accept when the window elapses. Disposing only
    /// kills the timer; the token source is deliberately left to the GC so a
    /// deadline firing concurrently with disposal (a connection was just
    /// accepted) cancels a token nobody observes any more instead of throwing
    /// <see cref="ObjectDisposedException"/> on the timer thread.</summary>
    private sealed class IdleWindow : IDisposable
    {
        private readonly CancellationTokenSource _elapsed = new();
        private readonly ITimer _timer;

        internal IdleWindow(TimeProvider timeProvider, TimeSpan timeout)
        {
            _timer = timeProvider.CreateTimer(
                static state => ((IdleWindow)state!)._elapsed.Cancel(),
                this,
                timeout,
                Timeout.InfiniteTimeSpan);
        }

        internal CancellationToken Token => _elapsed.Token;

        internal bool HasElapsed => _elapsed.IsCancellationRequested;

        public void Dispose() => _timer.Dispose();
    }
}
