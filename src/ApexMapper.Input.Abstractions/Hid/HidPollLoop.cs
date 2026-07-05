using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;

namespace ApexMapper.Input.Abstractions.Hid;

public sealed class HidPollLoop : IAsyncDisposable
{
    private readonly IHidStream _stream;
    private readonly HidReportParser _parser;
    private readonly KeyStateStore _store;
    private readonly int _reportLength;
    private readonly int _consecutiveFailureThreshold;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _statusLock = new();

    private Thread? _thread;
    private TaskCompletionSource? _startedTcs;
    private BackendStatus _status = BackendStatus.Stopped;
    private long _readCount;
    private long _failureCount;
    private int _consecutiveFailures;
    private int _disposed;

    public HidPollLoop(
        IHidStream stream,
        HidReportParser parser,
        KeyStateStore store,
        int reportLength,
        int consecutiveFailureThreshold = 5)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(store);
        if (reportLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reportLength), "reportLength must be positive.");
        }
        if (consecutiveFailureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailureThreshold), "threshold must be positive.");
        }

        _stream = stream;
        _parser = parser;
        _store = store;
        _reportLength = reportLength;
        _consecutiveFailureThreshold = consecutiveFailureThreshold;
    }

    public BackendStatus Status
    {
        get { lock (_statusLock) { return _status; } }
    }

    public long ReadCount => Interlocked.Read(ref _readCount);

    public long FailureCount => Interlocked.Read(ref _failureCount);

    public event EventHandler<BackendStatusChanged>? StatusChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_thread is not null)
        {
            return Task.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();

        _startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TransitionStatus(BackendStatus.Starting, reason: null);

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "HidPollLoop",
        };
        _thread.Start();

        return _startedTcs.Task;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_thread is null)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        var thread = _thread;
        // Join off the caller's thread with a 1s deadline.
        await Task.Run(() => thread.Join(TimeSpan.FromSeconds(1)), ct).ConfigureAwait(false);
        _thread = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private void Loop()
    {
        var buffer = new byte[_reportLength];
        var ct = _cts.Token;

        TransitionStatus(BackendStatus.Running, reason: null);
        _startedTcs?.TrySetResult();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var n = _stream.Read(buffer);
                    if (n <= 0)
                    {
                        // Idle, not dead: a zero-byte read means the device had no
                        // report ready this tick (a healthy blocking stream returns 0
                        // when its read timeout elapses). A quiet device must not be
                        // mistaken for a broken one, so idle ticks are silent — they
                        // neither count as failures nor reset the streak. A genuinely
                        // dead stream throws, which is what trips FaultedAnalog below.
                    }
                    else
                    {
                        _consecutiveFailures = 0;
                        _parser.ParseInto(buffer.AsSpan(0, n), _store);
                        Interlocked.Increment(ref _readCount);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _failureCount);
                    _consecutiveFailures++;
                }

                if (_consecutiveFailures >= _consecutiveFailureThreshold)
                {
                    TransitionStatus(
                        BackendStatus.FaultedAnalog,
                        $"hid read failed {_consecutiveFailures} times in a row");
                    return;
                }
            }
        }
        finally
        {
            if (Status != BackendStatus.FaultedAnalog)
            {
                TransitionStatus(BackendStatus.Stopped, reason: null);
            }
        }
    }

    private void TransitionStatus(BackendStatus next, string? reason)
    {
        lock (_statusLock)
        {
            if (_status == next)
            {
                return;
            }
            _status = next;
        }
        StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.HidAnalog, next, reason));
    }
}
