using System.IO.Pipes;
using ApexMapper.Core.Pipeline;

namespace ApexMapper.Output.Ipc;

/// <summary>
/// Tray-side channel to the supervisor over a per-session named pipe. Provides
/// the raw send primitives (control / heartbeat / zero / panic); the send
/// <em>cadence</em> — the 100 ms control and 250 ms heartbeat timers — belongs to
/// the caller, not here. This class also owns no retry policy: a fault or pipe
/// break drops the connection and raises <see cref="Disconnected"/> once, and
/// reconnection is the responsibility of the App-side channel adapter.
///
/// Control and heartbeat frames carry a process-monotonic sequence number and a
/// timestamp from the injected <see cref="TimeProvider"/>. SchemaVersion is
/// stamped by the underlying connection on send.
/// </summary>
public sealed class SupervisorClient : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly TimeProvider _timeProvider;
    private readonly FrameCodec _codec = new();

    private FrameConnection? _connection;
    private long _sequence;
    private int _connected;
    private int _disposed;

    public SupervisorClient(string sessionId, TimeSpan? connectTimeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _connect = DefaultConnect(sessionId, connectTimeout ?? TimeSpan.FromSeconds(2));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised once when the connection drops (clean disconnect or fault). The
    /// argument carries the fault cause, or null for a clean peer disconnect.</summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (IsConnected)
        {
            throw new InvalidOperationException("Already connected.");
        }

        Stream stream = await _connect(cancellationToken).ConfigureAwait(false);
        var connection = new FrameConnection(stream, _codec);
        connection.Faulted += OnConnectionFaulted;
        _connection = connection;
        Volatile.Write(ref _connected, 1);

        // The supervisor sends nothing today; the loop exists to detect the peer
        // closing the pipe. It outlives the connect token — DisposeAsync stops it.
        Task readLoop = connection.RunReadLoopAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        _ = readLoop.ContinueWith(
            _ => HandleDisconnect(null),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public Task SubmitControlAsync(PadStatePayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var frame = new ControlFrame
        {
            SequenceNumber = NextSequence(),
            TimestampTicks = NowTicks(),
            Payload = payload,
        };
        return SendAsync(frame, cancellationToken);
    }

    public Task SubmitHeartbeatAsync(CancellationToken cancellationToken)
    {
        var frame = new HeartbeatFrame
        {
            SequenceNumber = NextSequence(),
            TimestampTicks = NowTicks(),
        };
        return SendAsync(frame, cancellationToken);
    }

    public Task SubmitZeroAsync(string? reason, CancellationToken cancellationToken)
    {
        var frame = new ZeroFrame
        {
            TimestampTicks = NowTicks(),
            Reason = reason,
        };
        return SendAsync(frame, cancellationToken);
    }

    public Task SubmitPanicAsync(string? reason, CancellationToken cancellationToken)
    {
        var frame = new PanicFrame
        {
            TimestampTicks = NowTicks(),
            Reason = reason,
        };
        return SendAsync(frame, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Zero the connected flag first so the loop's completion continuation does
        // not surface a Disconnected event for an owner-initiated shutdown.
        Interlocked.Exchange(ref _connected, 0);
        FrameConnection? connection = _connection;
        if (connection is not null)
        {
            connection.Faulted -= OnConnectionFaulted;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendAsync(IFrame frame, CancellationToken cancellationToken)
    {
        FrameConnection? connection = _connection;
        if (connection is null || !IsConnected)
        {
            throw new InvalidOperationException("Not connected to the supervisor.");
        }

        try
        {
            await connection.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HandleDisconnect(ex is InvalidOperationException ? null : ex);
            throw;
        }
    }

    private void OnConnectionFaulted(Exception cause) => HandleDisconnect(cause);

    private void HandleDisconnect(Exception? error)
    {
        if (Interlocked.Exchange(ref _connected, 0) == 0)
        {
            return; // already disconnected, or never connected
        }

        Disconnected?.Invoke(error);
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private long NowTicks() => _timeProvider.GetUtcNow().UtcTicks;

    private static Func<CancellationToken, Task<Stream>> DefaultConnect(string sessionId, TimeSpan timeout) =>
        async cancellationToken =>
        {
            var pipe = new NamedPipeClientStream(
                ".", PipeNames.ForSession(sessionId), PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return pipe;
        };
}
