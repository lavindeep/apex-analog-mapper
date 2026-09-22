using ApexMapper.Core.Keys;

namespace ApexMapper.Windows.Input;

/// <summary>
/// The swallow rules from the design's blocking section, as a pure decision over
/// per-slot state so every rule has a test without a hook. <see cref="Decide"/>,
/// <see cref="IsStopChord"/> and <see cref="SyncModifiers"/> run on the hook thread;
/// <see cref="ForegroundLost"/> may run on the foreground tracker's thread; the per-slot
/// state is a byte array written with volatile stores and the modifier bits are a
/// volatile int, so nothing takes a lock. <see cref="SetMapped"/> runs only while no
/// hook is installed.
///
/// Rules: swallow a mapped key only while the game is foreground; never swallow
/// injected events (outside test mode); a key-up is swallowed only if its key-down was
/// swallowed and the game is still foreground (the extra term covers the gap between
/// the flag dropping and <see cref="ForegroundLost"/>, and fails in the safe direction);
/// keys physically down at install are marked passed; a Ctrl, Alt, or Win chord passes
/// through and releases held mapped keys; on foreground loss, swallowed-down keys are
/// marked passed so their key-up reaches the desktop, and a repeat after the flag
/// dropped passes too. The auto-repeat of a mapped key whose down passed (pressed on
/// the desktop, then focus moved to the game) is swallowed while the game has focus and
/// no modifier is down; its key-up still passes to whoever got the down.
/// </summary>
public sealed class HookPolicy
{
    private const byte Up = 0;
    private const byte SwallowedDown = 1;
    private const byte PassedDown = 2;

    public const int LeftCtrlSlot = 0x1D;
    public const int RightCtrlSlot = 256 + 0x1D;
    public const int LeftAltSlot = 0x38;
    public const int RightAltSlot = 256 + 0x38;
    public const int LeftWinSlot = 256 + 0x5B;
    public const int RightWinSlot = 256 + 0x5C;
    public const int F12Slot = 0x58;

    public const int LeftCtrlBit = 0b1;
    public const int RightCtrlBit = 0b10;
    public const int LeftAltBit = 0b100;
    public const int RightAltBit = 0b1000;
    public const int LeftWinBit = 0b10000;
    public const int RightWinBit = 0b100000;

    private readonly byte[] _state = new byte[ScanCode.SlotCount];
    private readonly bool[] _mapped = new bool[ScanCode.SlotCount];
    private int _modifiers;
    private int _swallowed;

    /// <summary>Test mode: injected events are treated like physical ones so a synthetic press can prove the hook.</summary>
    public bool SwallowInjected { get; set; }

    /// <summary>Events swallowed so far, for the hardware test and the status card.</summary>
    public int SwallowedCount => Volatile.Read(ref _swallowed);

    public int Modifiers => Volatile.Read(ref _modifiers);

    public bool CtrlDown => (Modifiers & (LeftCtrlBit | RightCtrlBit)) != 0;

    public bool AltDown => (Modifiers & (LeftAltBit | RightAltBit)) != 0;

    public bool WinDown => (Modifiers & (LeftWinBit | RightWinBit)) != 0;

    /// <summary>Replaces the set of keys the session blocks. Only while no hook is installed: the callback reads the map without a lock.</summary>
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

    /// <summary>
    /// Replaces the modifier bits with what the keyboard state says. Called at install
    /// and from the hook thread's timer, never from the callback (the asynchronous key
    /// state is not updated yet when the callback runs). Without this, a modifier
    /// whose key-up the hook never sees (Ctrl+Alt+Del, a UAC prompt) sticks for the
    /// session: nothing is swallowed and a bare F12 stops the mapper.
    /// </summary>
    public void SyncModifiers(int bits) => Volatile.Write(ref _modifiers, bits & 0b111111);

    /// <summary>
    /// Ctrl+Alt+F12, edge-triggered, from the modifiers this policy has seen. Left Alt
    /// only: AltGr arrives as left Ctrl plus right Alt on non-US layouts and must not
    /// stop the session. Injected events count only in test mode. On a hit the F12 slot
    /// is recorded as swallowed-down so its key-up is swallowed too.
    /// </summary>
    public bool IsStopChord(int slot, bool down, bool injected)
    {
        if (slot != F12Slot || !down || (injected && !SwallowInjected))
        {
            return false;
        }
        var modifiers = Modifiers;
        if (!CtrlDown || (modifiers & LeftAltBit) == 0 || Volatile.Read(ref _state[slot]) != Up)
        {
            return false;
        }
        Volatile.Write(ref _state[slot], SwallowedDown);
        Interlocked.Increment(ref _swallowed);
        return true;
    }

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
                Volatile.Write(ref _modifiers, Modifiers | modifierBit);
                ReleaseHeld();
            }
            else
            {
                Volatile.Write(ref _modifiers, Modifiers & ~modifierBit);
            }
            return false;
        }
        if (down)
        {
            switch (Volatile.Read(ref _state[slot]))
            {
                case SwallowedDown:
                    if (gameForeground)
                    {
                        return Count(true);
                    }
                    Volatile.Write(ref _state[slot], PassedDown);
                    return false;
                case PassedDown:
                    return Count(_mapped[slot] && gameForeground && Modifiers == 0);
                default:
                    var swallow = _mapped[slot] && gameForeground && Modifiers == 0;
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
        Volatile.Write(ref _modifiers, 0);
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

    public static int ModifierBit(int slot) => slot switch
    {
        LeftCtrlSlot => LeftCtrlBit,
        RightCtrlSlot => RightCtrlBit,
        LeftAltSlot => LeftAltBit,
        RightAltSlot => RightAltBit,
        LeftWinSlot => LeftWinBit,
        RightWinSlot => RightWinBit,
        _ => 0,
    };
}
