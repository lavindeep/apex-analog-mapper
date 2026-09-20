using ApexMapper.Core.Keys;

namespace ApexMapper.Core.Engine;

/// <summary>
/// Decides, per analog-driven key and per tick, whether the sensor or the hook drives
/// it. The sensor drives it once the sensor path is available and has read the key at
/// rest at least once (the at-rest latch). When the sensor path drops out the key
/// falls back to the hook's digital state; when it comes back the key stays digital
/// until it has been read at rest again, so the hand-over always happens at zero.
/// </summary>
public struct SourceSelector
{
    private bool _latched;

    public bool UsingAnalog { get; private set; }

    public void Reset()
    {
        _latched = false;
        UsingAnalog = false;
    }

    /// <summary>Returns the source value in 0..1: sensor depth, or 1/0 from the hook.</summary>
    public float Select(in KeySlot slot, bool analogAvailable)
    {
        if (!analogAvailable || float.IsNaN(slot.Analog))
        {
            _latched = false;
            UsingAnalog = false;
            return slot.Digital ? 1f : 0f;
        }
        if (!_latched && slot.AnalogAtRest)
        {
            _latched = true;
        }
        UsingAnalog = _latched;
        return _latched ? slot.Analog : slot.Digital ? 1f : 0f;
    }
}
