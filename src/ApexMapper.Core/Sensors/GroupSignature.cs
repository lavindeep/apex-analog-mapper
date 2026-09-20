namespace ApexMapper.Core.Sensors;

/// <summary>
/// Which of a group's fourteen slots have no sensor, learned from a reading at rest
/// (under 50 counts). Because replies carry no command echo, this is how the poller
/// tells a reply for the right group from a shifted one: the pattern of absent slots
/// differs between groups on every layout seen so far. A firmware reply parsed as a
/// group fails too, because its ASCII bytes read as values above 4095.
/// </summary>
public readonly record struct GroupSignature(ushort AbsentMask)
{
    private const int SlotCount = SensorProtocol.SensorsPerGroup;

    public static GroupSignature FromRest(ReadOnlySpan<ushort> raw)
    {
        ushort mask = 0;
        for (var i = 0; i < SlotCount; i++)
        {
            if (!SensorProtocol.HasSensor(raw[i]))
            {
                mask |= (ushort)(1 << i);
            }
        }
        return new GroupSignature(mask);
    }

    public bool IsAbsent(int slot) => (AbsentMask & (1 << slot)) != 0;

    /// <summary>
    /// True when every absent slot reads absent, every present slot reads present, and
    /// no value exceeds the 12-bit range. A held key never reads under 50, so pressing
    /// keys cannot cause a false mismatch.
    /// </summary>
    public bool Matches(ReadOnlySpan<ushort> raw)
    {
        for (var i = 0; i < SlotCount; i++)
        {
            var value = raw[i];
            if (value > SensorProtocol.MaxCount)
            {
                return false;
            }
            if (SensorProtocol.HasSensor(value) == IsAbsent(i))
            {
                return false;
            }
        }
        return true;
    }
}
