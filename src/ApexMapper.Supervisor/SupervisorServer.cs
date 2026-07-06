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
/// creation failures back off instead of spinning hot. Only
/// <see cref="StopAsync"/> ends the loop — an active session is then torn down
/// (<see cref="SessionEndReason.Shutdown"/>: pad zeroed and disconnected).
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

    private Task _loop = Task.CompletedTask;
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

    private async Task RunLoopAsync(CancellationToken stopToken)
    {
        var consecutiveFailures = 0;
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
                    break;
                }

                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await DisposeQuietlyAsync(pipe).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                await DisposeQuietlyAsync(pipe).ConfigureAwait(false);
                Interlocked.Increment(ref _pipeFailures);
                RaiseDiagnostics($"accept failed: {ex.Message}");
                consecutiveFailures++;
                if (!await BackoffAsync(consecutiveFailures, stopToken).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            consecutiveFailures = 0;
            await RunOneSessionAsync(pipe, stopToken).ConfigureAwait(false);
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
}
