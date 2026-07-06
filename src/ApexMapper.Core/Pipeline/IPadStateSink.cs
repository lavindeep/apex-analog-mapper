namespace ApexMapper.Core.Pipeline;

/// <summary>
/// Receives the pad state produced by each mapping tick. Implemented by the
/// output-side channel (which owns the send cadence to the supervisor); the
/// mapping engine only hands over the latest state and never blocks on
/// delivery, so <see cref="Push"/> must be cheap, non-blocking, and safe to
/// call at the engine's tick rate from the engine's tick thread.
/// </summary>
public interface IPadStateSink
{
    void Push(in VirtualPadState state);
}
