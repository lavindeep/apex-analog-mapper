using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Backends;

namespace ApexMapper.Input.Abstractions.Hid;

public sealed class HidPollLoop : IAsyncDisposable
{
    private readonly IHidStream _stream;
    private readonly HidReportParser _parser;
    private readonly KeyStateStore _store;
    private readonly int _reportLength;
    private readonly int _consecutiveFailureThreshold;
    private readonly HidReportType _reportType;
    private readonly int _featurePollIntervalMs;
    private readonly byte _reportId;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _statusLock = new();

    private Thread? _thread;
    private TaskCompletionSource? _startedTcs;
    private BackendStatus _status = BackendStatus.Stopped;
    private long _readCount;
    private long _failureCount;
    private int _consecutiveFailures;
    private int _disposed;

    /// <param name="reportType">
    /// Whether the analog payload arrives as an input report (read from the
    /// stream, blocking until data or a timeout) or a feature report (polled
    /// with GetFeature). Feature polling is the exploratory path for devices
    /// like the Apex Pro that expose analog travel only through a feature report.
    /// </param>
    /// <param name="featurePollIntervalMs">
    /// Delay between feature-report polls, so feature mode does not spin a core.
    /// Ignored for input reports (their read blocks). Zero polls continuously.
    /// </param>
    /// <param name="reportId">
    /// The HID report id to request in feature mode. HidD_GetFeature identifies a
    /// numbered report by the leading buffer byte, so a non-zero id is seeded into
    /// buffer[0] before each GetFeature; id 0 (unnumbered) leaves it untouched.
    /// Unused in input mode.
    /// </param>
    public HidPollLoop(
        IHidStream stream,
        HidReportParser parser,
        KeyStateStore store,
        int reportLength,
        int consecutiveFailureThreshold = 5,
        HidReportType reportType = HidReportType.Input,
        int featurePollIntervalMs = 2,
        byte reportId = 0)
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
        if (featurePollIntervalMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(featurePollIntervalMs), "interval must be non-negative.");
        }

        _stream = stream;
        _parser = parser;
        _store = store;
        _reportLength = reportLength;
        _consecutiveFailureThreshold = consecutiveFailureThreshold;
        _reportType = reportType;
        _featurePollIntervalMs = featurePollIntervalMs;
        _reportId = reportId;
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
                    // Route by report type: input reports are read from the stream
                    // (blocking, 0 == idle); feature reports are polled with
                    // GetFeature, which fills the whole buffer or throws.
                    int n;
                    if (_reportType == HidReportType.Feature)
                    {
                        // Numbered feature reports are selected by the leading byte;
                        // seed it so HidD_GetFeature requests the declared report id
                        // rather than id 0. Unnumbered reports (id 0) leave it alone.
                        if (_reportId != 0)
                        {
                            buffer[0] = _reportId;
                        }
                        _stream.GetFeature(buffer);
                        n = buffer.Length;
                    }
                    else
                    {
                        n = _stream.Read(buffer);
                    }

                    if (n <= 0)
                    {
                        // Idle, not dead: a zero-byte read means the device had no
                        // report ready this tick. The stream adapter normalizes a
                        // read timeout to a 0-byte read, so a quiet device must not
                        // be mistaken for a broken one; idle ticks are silent — they
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

                // Feature polling has no blocking read to pace it; sleep between
                // polls (cancellably) so it does not peg a core. Input reads block.
                if (_reportType == HidReportType.Feature && _featurePollIntervalMs > 0)
                {
                    ct.WaitHandle.WaitOne(_featurePollIntervalMs);
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
