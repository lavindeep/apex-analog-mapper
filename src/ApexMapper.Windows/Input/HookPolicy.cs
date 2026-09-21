using ApexMapper.Core.Keys;

namespace ApexMapper.Windows.Input;

/// <summary>
/// The swallow rules from the design's blocking section, as a pure decision over
/// per-slot state so every rule has a test without a hook. Called only from the hook
/// thread except <see cref="ForegroundLost"/>, which the foreground tracker calls; the
/// per-slot state is a byte array written with volatile stores, so nothing takes a lock.
///
/// Rules: swallow a mapped key only while the game is foreground; never swallow
/// injected events (outside test mode); a key-up is swallowed only if its key-down was
/// swallowed; keys physically down at install are marked passed; a Ctrl, Alt, or Win
/// chord passes through and releases held mapped keys; on foreground loss,
/// swallowed-down keys are marked passed so their key-up reaches the desktop.
/// </summary>
public sealed class HookPolicy
{
    private const byte Up = 0;
    private const byte SwallowedDown = 1;
    private const byte PassedDown = 2;

    private const int LeftCtrl = 0x1D;
    private const int RightCtrl = 256 + 0x1D;
    private const int LeftAlt = 0x38;
    private const int RightAlt = 256 + 0x38;
    private const int LeftWin = 256 + 0x5B;
    private const int RightWin = 256 + 0x5C;
    private const int F12 = 0x58;

    private readonly byte[] _state = new byte[ScanCode.SlotCount];
    private readonly bool[] _mapped = new bool[ScanCode.SlotCount];
    private int _modifiers;
    private int _swallowed;

    /// <summary>Test mode: injected events are treated like physical ones so a synthetic press can prove the hook.</summary>
    public bool SwallowInjected { get; set; }

    /// <summary>Events swallowed so far, for the hardware test and the status card.</summary>
    public int SwallowedCount => Volatile.Read(ref _swallowed);

    public bool CtrlDown => (_modifiers & 0b11) != 0;

    public bool AltDown => (_modifiers & 0b1100) != 0;

    public bool WinDown => (_modifiers & 0b110000) != 0;

    /// <summary>Replaces the set of keys the session blocks. Reserved keys are refused by the profile validator before this.</summary>
    public void SetMapped(IEnumerable<ScanCode> keys)
    {
        Array.Clear(_mapped);
        foreach (var key in keys)
        {
            _mapped[key.Slot] = true;
        }
    }

    public bool IsMapped(int slot) => _mapped[slot];

    public bool IsSwallowedDown(int slot) => Volatile.Read(ref _state[slot]) == SwallowedDown;

    /// <summary>The key was physically down when the hook installed: its release must reach the desktop.</summary>
    public void MarkDown(int slot) => Volatile.Write(ref _state[slot], PassedDown);

    /// <summary>Ctrl+Alt+F12, decided from the modifiers this policy has seen go down.</summary>
    public bool IsStopChord(int slot, bool down) => slot == F12 && down && CtrlDown && AltDown;

    /// <summary>True to swallow the event.</summary>
    public bool Decide(int slot, bool down, bool injected, bool gameForeground)
    {
        if (injected && !SwallowInjected)
        {
            return false;
        }
        var modifierBit = ModifierBit(slot);
        if (modifierBit != 0)
        {
            if (down)
            {
                _modifiers |= modifierBit;
                ReleaseHeld();
            }
            else
            {
                _modifiers &= ~modifierBit;
            }
            return false;
        }
        if (down)
        {
            switch (Volatile.Read(ref _state[slot]))
            {
                case SwallowedDown:
                    return Count(true);
                case PassedDown:
                    return false;
                default:
                    var swallow = _mapped[slot] && gameForeground && _modifiers == 0;
                    Volatile.Write(ref _state[slot], swallow ? SwallowedDown : PassedDown);
                    return Count(swallow);
            }
        }
        var was = Volatile.Read(ref _state[slot]);
        Volatile.Write(ref _state[slot], Up);
        return Count(was == SwallowedDown && gameForeground);
    }

    /// <summary>Every swallowed-down key becomes passed-down so its eventual key-up reaches the desktop. Nothing is injected.</summary>
    public void ForegroundLost() => ReleaseHeld();

    /// <summary>Hook uninstalled: forget everything.</summary>
    public void Reset()
    {
        Array.Clear(_state);
        _modifiers = 0;
    }

    private void ReleaseHeld()
    {
        for (var slot = 0; slot < _state.Length; slot++)
        {
            if (Volatile.Read(ref _state[slot]) == SwallowedDown)
            {
                Volatile.Write(ref _state[slot], PassedDown);
            }
        }
    }

    private bool Count(bool swallow)
    {
        if (swallow)
        {
            Interlocked.Increment(ref _swallowed);
        }
        return swallow;
    }

    private static int ModifierBit(int slot) => slot switch
    {
        LeftCtrl => 0b1,
        RightCtrl => 0b10,
        LeftAlt => 0b100,
        RightAlt => 0b1000,
        LeftWin => 0b10000,
        RightWin => 0b100000,
        _ => 0,
    };
}
