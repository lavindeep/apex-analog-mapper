using ApexMapper.Core.Diagnostics;

namespace ApexMapper.App.Diagnostics.Latency;

/// <summary>
/// Periodically drains the binding pipeline's <see cref="LatencyRecorder"/>
/// onto a background thread, feeds each sample into an
/// <see cref="HdrHistogramAdapter"/>, and exposes running P50/P95/P99 and a
/// <see cref="SamplesAdded"/> event for downstream consumers (view-models,
/// chart adapters).
///
/// <para>
/// The sampler owns its own thread (rather than a <see cref="System.Threading.Timer"/>
/// or <see cref="PeriodicTimer"/>) so that an arbitrarily-long drain still
/// preserves the requested interval pacing via a per-iteration deadline; this
/// keeps drift bounded under load and avoids capture of execution-context
/// allocations on each timer fire.
/// </para>
/// </summary>
public sealed class LatencySampler : ILatencySampler, IDisposable
{
    private readonly LatencyRecorder _recorder;
    private readonly long[] _buffer;
    private readonly HdrHistogramAdapter _histogram = new();
    private readonly LatencySample[] _eventBuffer;
    private readonly BatchView _batchView;
    private long _wallClockBaseMicros;

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private int _running;

    /// <summary>
    /// Creates a sampler bound to <paramref name="recorder"/>. The drain
    /// <paramref name="bufferSize"/> must be a positive power of two and bounds
    /// the maximum samples consumed per tick.
    /// </summary>
    public LatencySampler(LatencyRecorder recorder, int bufferSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (bufferSize <= 0 || (bufferSize & (bufferSize - 1)) != 0)
        {
            throw new ArgumentException("Buffer size must be a positive power of two.", nameof(bufferSize));
        }

        _recorder = recorder;
        _buffer = new long[bufferSize];
        _eventBuffer = new LatencySample[bufferSize];
        _batchView = new BatchView(_eventBuffer);
    }

    /// <inheritdoc />
    public (double P50, double P95, double P99) Percentiles => _histogram.Percentiles();

    /// <inheritdoc />
    public event Action<IReadOnlyList<LatencySample>>? SamplesAdded;

    /// <summary>Read-only view of the running histogram for chart binding.</summary>
    public IReadOnlyList<long> Buckets => _histogram.Buckets;

    /// <inheritdoc />
    public void Start(TimeSpan interval, CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("Sampler is already running.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _wallClockBaseMicros = NowMicros();
        var token = _cts.Token;
        _thread = new Thread(() => Run(interval, token))
        {
            IsBackground = true,
            Name = "ApexMapper.LatencySampler",
        };
        _thread.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed in a concurrent Stop; nothing to do.
        }

        var thread = Interlocked.Exchange(ref _thread, null);
        thread?.Join(TimeSpan.FromSeconds(1));
        cts.Dispose();
        Interlocked.Exchange(ref _running, 0);
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void Run(TimeSpan interval, CancellationToken token)
    {
        var lastWriteCount = _recorder.WriteCount;
        var nextDeadline = Environment.TickCount64 + (long)interval.TotalMilliseconds;

        while (!token.IsCancellationRequested)
        {
            DrainOnce(ref lastWriteCount);

            // Sleep until the next deadline; if we overran, fire immediately.
            var now = Environment.TickCount64;
            var sleep = nextDeadline - now;
            if (sleep > 0)
            {
                try
                {
                    if (token.WaitHandle.WaitOne((int)sleep))
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
            nextDeadline += (long)interval.TotalMilliseconds;
            if (nextDeadline < now)
            {
                // Recover from large skips (e.g. debugger pause) by re-anchoring.
                nextDeadline = now + (long)interval.TotalMilliseconds;
            }
        }
    }

    private void DrainOnce(ref long lastWriteCount)
    {
        var currentWriteCount = _recorder.WriteCount;
        if (currentWriteCount == lastWriteCount)
        {
            return;
        }

        var copied = _recorder.TrySnapshot(_buffer);
        if (copied <= 0)
        {
            return;
        }

        // The recorder hands us the most-recent <copied> samples. We only want
        // the *new* ones; in the steady-state case the producer rate is below
        // the buffer capacity and `copied` matches the delta exactly. If the
        // producer outran us we accept the dropped samples (the ring buffer
        // overwrote them) and simply ingest what's available.
        var delta = currentWriteCount - lastWriteCount;
        var newCount = (int)Math.Min(delta, copied);
        var startInBuffer = copied - newCount;

        // Timestamps are best-effort: the recorder doesn't preserve per-sample
        // wall-clock, so we tag the batch with the drain time. Consumers that
        // need finer ordering can rely on event-emission order which matches
        // recorder write order.
        var batchTimestamp = NowMicros() - _wallClockBaseMicros;
        for (var i = 0; i < newCount; i++)
        {
            var latency = _buffer[startInBuffer + i];
            _histogram.RecordMicros(latency);
            _eventBuffer[i] = new LatencySample(batchTimestamp, latency);
        }
        lastWriteCount = currentWriteCount;

        var handler = SamplesAdded;
        if (handler is not null && newCount > 0)
        {
            _batchView.SetCount(newCount);
            handler(_batchView);
        }
    }

    /// <summary>
    /// Mutable, reusable IReadOnlyList view over <see cref="LatencySampler._eventBuffer"/>.
    /// Allocating one of these once per sampler avoids per-drain garbage from
    /// boxing <c>ArraySegment&lt;LatencySample&gt;</c> to <c>IReadOnlyList</c>.
    /// Consumers must not retain the reference across <see cref="ILatencySampler.SamplesAdded"/>
    /// invocations; its contents are overwritten on each drain.
    /// <para>
    /// <see cref="GetEnumerator"/> returns an <see cref="Enumerator"/> struct so
    /// that <c>foreach</c> (which binds to the duck-typed public method first)
    /// is allocation-free. The interface implementations of
    /// <see cref="IEnumerable{T}.GetEnumerator"/> and
    /// <see cref="System.Collections.IEnumerable.GetEnumerator"/> still box the struct; LINQ
    /// operators such as <c>Sum</c> or <c>ToList</c> reach the enumerator
    /// through those interfaces and therefore allocate. The hot drain path
    /// passes the batch through <c>foreach</c>/indexer access only, so those
    /// box paths are not exercised in production.
    /// </para>
    /// </summary>
    private sealed class BatchView : IReadOnlyList<LatencySample>
    {
        private readonly LatencySample[] _buffer;

        public BatchView(LatencySample[] buffer) { _buffer = buffer; }

        public int Count { get; private set; }

        internal void SetCount(int count) => Count = count;

        public LatencySample this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return _buffer[index];
            }
        }

        /// <summary>Returns a struct enumerator — <c>foreach</c> uses this and avoids allocation.</summary>
        public Enumerator GetEnumerator() => new(this);

        IEnumerator<LatencySample> IEnumerable<LatencySample>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Struct enumerator over the populated prefix of the backing buffer.</summary>
        public struct Enumerator : IEnumerator<LatencySample>
        {
            private readonly BatchView _view;
            private int _index;

            internal Enumerator(BatchView view)
            {
                _view = view;
                _index = -1;
            }

            public LatencySample Current => _view._buffer[_index];

            object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext()
            {
                _index++;
                return _index < _view.Count;
            }

            public void Reset() => _index = -1;

            public void Dispose() { }
        }
    }

    private static long NowMicros()
    {
        // Microsecond-resolution monotonic timestamp from Stopwatch ticks.
        var ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        var freq = System.Diagnostics.Stopwatch.Frequency;
        // (ticks * 1_000_000) / freq, using 128-bit-ish math to avoid overflow.
        // ticks fits well below 2^63 for any practical runtime; cast to double
        // is fine for the timestamp display channel where we don't need µs-exact.
        return (long)(ticks * (1_000_000.0 / freq));
    }
}
