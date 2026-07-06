namespace ApexMapper.Output.Ipc;

/// <summary>
/// Owns one duplex <see cref="Stream"/> and pumps <see cref="IFrame"/>s across
/// it: a single reader loop plus writes serialized through a semaphore so
/// concurrent <see cref="SendAsync"/> callers never interleave bytes.
///
/// Fail-closed: any read, write, or protocol failure transitions the connection
/// to <see cref="IsFaulted"/> exactly once, raises <see cref="Faulted"/> with the
/// cause, and makes every subsequent send throw fast. A fault never wedges — the
/// owner observes it via the event and the completed read loop and can react.
///
/// A received frame whose version is unknown (0, or newer than this build) is
/// dropped and counted (<see cref="UnknownVersionFrames"/>), not faulted, so a
/// forward-compatible peer cannot take the connection down.
/// </summary>
public sealed class FrameConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly FrameCodec _codec;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private long _unknownVersionFrames;
    private int _faulted;
    private int _readLoopStarted;
    private int _disposed;

    public FrameConnection(Stream stream, FrameCodec? codec = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _codec = codec ?? new FrameCodec();
    }

    /// <summary>Raised once, with the causing exception, when the connection faults.</summary>
    public event Action<Exception>? Faulted;

    public bool IsFaulted => Volatile.Read(ref _faulted) == 1;

    /// <summary>Count of received frames dropped because their version was unknown.</summary>
    public long UnknownVersionFrames => Interlocked.Read(ref _unknownVersionFrames);

    /// <summary>
    /// Runs the single reader loop until the peer closes cleanly, the connection
    /// is disposed, or a failure faults it. Failures are surfaced through
    /// <see cref="Faulted"/>; this task completes normally rather than throwing so
    /// a fire-and-forget owner never sees an unobserved exception. Calling twice
    /// throws — there is exactly one reader.
    /// </summary>
    public async Task RunReadLoopAsync(Func<IFrame, ValueTask> onFrame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (Interlocked.Exchange(ref _readLoopStarted, 1) == 1)
        {
            throw new InvalidOperationException("The read loop is already running; a connection has exactly one reader.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        try
        {
            while (true)
            {
                IFrame? frame;
                try
                {
                    frame = await _codec.ReadFrameAsync(_stream, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 1)
                {
                    break;
                }

                if (frame is null)
                {
                    break; // clean end-of-stream at a frame boundary
                }

                if (!FrameCodec.IsKnownVersion(frame))
                {
                    Interlocked.Increment(ref _unknownVersionFrames);
                    continue;
                }

                await onFrame(frame).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    /// <summary>
    /// Stamps <see cref="IFrame.CurrentSchemaVersion"/> and writes the frame,
    /// serialized against concurrent senders. Throws
    /// <see cref="InvalidOperationException"/> if the connection has already
    /// faulted; a write failure faults the connection and rethrows.
    /// </summary>
    public async Task SendAsync(IFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfFaulted();

        IFrame stamped = Stamp(frame);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            await _codec.WriteFrameAsync(_stream, stamped, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            Fault(ex);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposing the transport must not throw out of DisposeAsync.
        }

        _lifetimeCts.Dispose();
        _writeLock.Dispose();
    }

    private void ThrowIfFaulted()
    {
        if (IsFaulted)
        {
            throw new InvalidOperationException("The connection has faulted and can no longer send frames.");
        }
    }

    private void Fault(Exception cause)
    {
        if (Interlocked.CompareExchange(ref _faulted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with DisposeAsync; the loop is already unwinding.
        }

        Faulted?.Invoke(cause);
    }

    private static IFrame Stamp(IFrame frame) => frame switch
    {
        ControlFrame f => f with { SchemaVersion = IFrame.CurrentSchemaVersion },
        HeartbeatFrame f => f with { SchemaVersion = IFrame.CurrentSchemaVersion },
        ZeroFrame f => f with { SchemaVersion = IFrame.CurrentSchemaVersion },
        PanicFrame f => f with { SchemaVersion = IFrame.CurrentSchemaVersion },
        _ => throw new ArgumentException($"Unknown frame type {frame.GetType().Name}.", nameof(frame)),
    };
}
