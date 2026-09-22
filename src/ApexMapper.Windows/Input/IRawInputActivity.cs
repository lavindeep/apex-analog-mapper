namespace ApexMapper.Windows.Input;

/// <summary>What a session needs from Raw Input: the newest key event's time, for noticing a lost hook, and whether the pump still runs.</summary>
public interface IRawInputActivity
{
    /// <summary>OS time (ms since boot) of the newest keyboard event, or zero.</summary>
    uint LastEventTime { get; }

    bool IsRunning { get; }
}