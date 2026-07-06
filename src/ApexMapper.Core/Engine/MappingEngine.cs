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
/// Threading: <see cref="SetProfile"/> and <see cref="SetEnabled"/> may be
/// called from any thread. Ticks run on one thread; when the store is written
/// concurrently by input backends it must be the <see cref="KeyIndex"/>-backed
/// store (the dictionary-backed default is single-threaded only).
/// </summary>
public sealed class MappingEngine
{
    private readonly KeyStateStore _store;
    private readonly IPadStateSink _sink;

    private BindingPipeline? _pipeline;
    private int _enabled = 1;

    // Tick-thread-only state.
    private VirtualPadState _pad;
    private bool _zeroPushedWhileDisabled;

    public MappingEngine(KeyStateStore store, IPadStateSink sink)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
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

    internal void TickOnce(float dtMs)
    {
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
