using System.Diagnostics;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;

namespace ApexMapper.Core.Engine;

/// <summary>
/// Drives the mapping ticks: each tick reads the <see cref="KeyStateStore"/>
/// through the active profile's <see cref="BindingPipeline"/> and pushes the
/// resulting <see cref="VirtualPadState"/> to the sink.
///
/// Profile swaps are atomic: <see cref="SetProfile"/> builds the new pipeline
/// on the caller's thread and publishes it with a single reference write, so a
/// tick always runs against exactly one profile — never a torn mix of two.
///
/// Disabling never freezes the last state at the sink: the first disabled tick
/// pushes a zero state, then the engine idles until re-enabled. Store-level
/// held-key gating on disable is the host's policy, applied at the
/// <see cref="KeyStateStore"/>; this engine's own guarantee is only that a
/// disabled engine's sink ends at zero.
///
/// Ticks run on a dedicated thread paced by a cancellable ~1 ms wait (the
/// same idiom as the HID feature-poll loop): a timer callback cannot hold a
/// 1 ms cadence, and a spin loop would peg a core. The wall-clock resolution
/// of that wait is scheduler-dependent, so each tick passes the <em>measured</em>
/// elapsed time into the pipeline — ramps stay time-accurate even when a busy
/// scheduler stretches a tick. The engine is single-run: start it once, stop
/// it once; stopping joins the thread with a 2 s bound.
///
/// Threading: <see cref="SetProfile"/> and <see cref="SetEnabled"/> may be
/// called from any thread. Ticks run on one thread; when the store is written
/// concurrently by input backends it must be the <see cref="KeyIndex"/>-backed
/// store (the dictionary-backed default is single-threaded only).
/// </summary>
public sealed class MappingEngine : IAsyncDisposable
{
    private static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

    private readonly KeyStateStore _store;
    private readonly IPadStateSink _sink;
    private readonly Action? _preTick;
    private readonly int _tickIntervalMs;
    private readonly CancellationTokenSource _cts = new();

    private Thread? _thread;
    private TaskCompletionSource? _startedTcs;
    private BindingPipeline? _pipeline;
    private int _enabled = 1;
    private int _disposed;

    // Tick-thread-only state.
    private VirtualPadState _pad;
    private bool _zeroPushedWhileDisabled;

    /// <param name="preTick">
    /// Optional hook invoked at the start of every tick — enabled or disabled —
    /// before the pipeline reads the store. The host uses it to drain queued
    /// input events into the store so each tick maps the freshest state, and
    /// draining on disabled ticks keeps key releases flowing (a release must
    /// still clear its held-key gate while mapping is off). Runs on the tick
    /// thread: it must be cheap, allocation-free in steady state, and must not
    /// throw — a throwing hook takes the tick loop down (the loop's final zero
    /// still reaches the sink, and the unhandled exception surfaces loudly
    /// rather than being swallowed).
    /// </param>
    public MappingEngine(KeyStateStore store, IPadStateSink sink, int tickIntervalMs = 1, Action? preTick = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _preTick = preTick;
        if (tickIntervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickIntervalMs), "tick interval must be positive.");
        }

        _tickIntervalMs = tickIntervalMs;
    }

    public bool IsEnabled => Volatile.Read(ref _enabled) == 1;

    /// <summary>
    /// Enables or disables mapping. Takes effect on the next tick: the first
    /// disabled tick pushes a zero state exactly once, further disabled ticks
    /// push nothing, and enabling resumes normal mapped output.
    /// </summary>
    public void SetEnabled(bool enabled) => Volatile.Write(ref _enabled, enabled ? 1 : 0);

    /// <summary>
    /// Atomically replaces the active profile; takes effect on the next tick.
    /// Null clears the bindings, so subsequent ticks push a zero state. Ramp
    /// and SOCD state restart from rest in the new pipeline — a held key ramps
    /// up again rather than carrying over, which can only reduce output.
    /// </summary>
    public void SetProfile(Profile? profile)
    {
        var pipeline = profile is null
            ? null
            : new BindingPipeline(profile.SingleBindings, profile.AxisBindings);
        Volatile.Write(ref _pipeline, pipeline);
    }

    /// <summary>Starts the tick thread; completes once the loop is running.
    /// Idempotent while running. The engine cannot be restarted after a stop.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_thread is not null)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "MappingEngine",
        };
        _thread.Start();

        return _startedTcs.Task;
    }

    /// <summary>Stops the loop and joins the tick thread with a 2 s bound, off
    /// the caller's thread. The loop pushes a final zero state on the way out.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
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
        await Task.Run(() => thread.Join(TimeSpan.FromSeconds(2)), cancellationToken).ConfigureAwait(false);
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
        var ct = _cts.Token;
        _startedTcs?.TrySetResult();

        long previous = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Cancellable pacing wait; actual resolution is up to the
                // scheduler, which is why dt below is measured, not assumed.
                ct.WaitHandle.WaitOne(_tickIntervalMs);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                long now = Stopwatch.GetTimestamp();
                var dtMs = (float)((now - previous) * TimestampToMs);
                previous = now;
                TickOnce(dtMs);
            }
        }
        finally
        {
            // Safety: the loop never ends leaving a non-zero state latched at
            // the sink — the sink's owner would otherwise keep forwarding the
            // last mapped state with nothing left to refresh it.
            _pad = default;
            _sink.Push(in _pad);
        }
    }

    internal void TickOnce(float dtMs)
    {
        _preTick?.Invoke();

        if (Volatile.Read(ref _enabled) == 0)
        {
            if (!_zeroPushedWhileDisabled)
            {
                _pad = default;
                _sink.Push(in _pad);
                _zeroPushedWhileDisabled = true;
            }

            return;
        }

        _zeroPushedWhileDisabled = false;
        var pipeline = Volatile.Read(ref _pipeline);
        if (pipeline is null)
        {
            _pad = default;
        }
        else
        {
            pipeline.Tick(_store, dtMs, ref _pad);
        }

        _sink.Push(in _pad);
    }
}
