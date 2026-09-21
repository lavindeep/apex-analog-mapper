namespace ApexMapper.Windows.Input;

/// <summary>
/// "The selected game is foreground and its input is visible to us", written by the
/// foreground tracker, read by the hook callback. A single volatile int so the hook
/// never takes a lock or calls win32k.
/// </summary>
public sealed class ForegroundFlag
{
    private int _value;

    public bool IsGameForeground
    {
        get => Volatile.Read(ref _value) != 0;
        set => Volatile.Write(ref _value, value ? 1 : 0);
    }
}
