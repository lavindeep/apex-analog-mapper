namespace ApexMapper.Core.Keys;

/// <summary>What the store knows about one key at one instant.</summary>
/// <param name="Digital">The hook's last observation: key is down.</param>
/// <param name="Analog">Sensor depth in 0..1, or NaN when the sensor path is unavailable.</param>
/// <param name="Gated">Physical state was unknown at some transition and no release has been seen since.</param>
/// <param name="AnalogDriven">The active profile drives this key from the sensor.</param>
public readonly record struct KeySlot(bool Digital, float Analog, bool Gated, bool AnalogDriven)
{
    /// <summary>A fresh reading inside the calibration noise band normalises to exactly zero.</summary>
    public bool AnalogAtRest => Analog == 0f;
}

/// <summary>
/// One packed cell per scan code slot, written by the hook thread (digital) and the
/// engine thread (analog), read by the engine. Each cell holds both values so digital
/// fallback can read the hook's state at the instant the sensor drops out.
///
/// The gate bit marks a key whose physical state became unknown (session start,
/// return from alt-tab, hook install, keyboard reconnect). While gated the key
/// contributes nothing. Only an observed release clears it: for an analog-driven key a
/// sensor reading at rest, for any other key the hook's key-up. A hook key-up never
/// clears an analog-driven key, because with rapid trigger a key-up means the key
/// rose a fraction of a millimetre, not that it was released.
///
/// Every write is a compare-and-swap loop written out by hand: this runs on the hot
/// path and must not allocate.
/// </summary>
public sealed class KeyStateStore
{
    private const long DigitalBit = 1L << 32;
    private const long GatedBit = 1L << 33;
    private const long AnalogDrivenBit = 1L << 34;
    private const long AnalogMask = 0xFFFF_FFFFL;

    private static readonly long UnavailableBits = BitConverter.SingleToUInt32Bits(float.NaN);

    private readonly long[] _cells = new long[ScanCode.SlotCount];

    public KeyStateStore()
    {
        Array.Fill(_cells, UnavailableBits);
    }

    public KeySlot Read(int slot) => Unpack(Volatile.Read(ref _cells[slot]));

    public bool IsGated(int slot) => (Volatile.Read(ref _cells[slot]) & GatedBit) != 0;

    /// <summary>Hook thread. A key-up clears the gate of a digital key.</summary>
    public void SetDigital(int slot, bool down)
    {
        ref var cell = ref _cells[slot];
        while (true)
        {
            var current = Volatile.Read(ref cell);
            var next = down ? current | DigitalBit : current & ~DigitalBit;
            if (!down && (next & AnalogDrivenBit) == 0)
            {
                next &= ~GatedBit;
            }
            if (Commit(ref cell, current, next))
            {
                return;
            }
        }
    }

    /// <summary>Engine thread. A reading at rest (exactly zero) clears the gate of an analog-driven key.</summary>
    public void SetAnalog(int slot, float depth)
    {
        var bits = (long)BitConverter.SingleToUInt32Bits(depth);
        var atRest = depth == 0f;
        ref var cell = ref _cells[slot];
        while (true)
        {
            var current = Volatile.Read(ref cell);
            var next = (current & ~AnalogMask) | bits;
            if (atRest && (next & AnalogDrivenBit) != 0)
            {
                next &= ~GatedBit;
            }
            if (Commit(ref cell, current, next))
            {
                return;
            }
        }
    }

    /// <summary>Called from the profile at session start, before any gating.</summary>
    public void SetAnalogDriven(int slot, bool analogDriven)
    {
        ref var cell = ref _cells[slot];
        while (true)
        {
            var current = Volatile.Read(ref cell);
            var next = analogDriven ? current | AnalogDrivenBit : current & ~AnalogDrivenBit;
            if (Commit(ref cell, current, next))
            {
                return;
            }
        }
    }

    public void Gate(int slot)
    {
        ref var cell = ref _cells[slot];
        while (true)
        {
            var current = Volatile.Read(ref cell);
            if (Commit(ref cell, current, current | GatedBit))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Gates every key whose state is unknown: all analog-driven keys (the next at-rest
    /// reading clears them) and every digital key currently down. A digital key that is
    /// up is known to be up and stays ungated, otherwise it could never clear.
    /// </summary>
    public void GateUnknown()
    {
        for (var slot = 0; slot < _cells.Length; slot++)
        {
            ref var cell = ref _cells[slot];
            while (true)
            {
                var current = Volatile.Read(ref cell);
                var unknown = (current & (AnalogDrivenBit | DigitalBit)) != 0;
                if (Commit(ref cell, current, unknown ? current | GatedBit : current))
                {
                    break;
                }
            }
        }
    }

    /// <summary>Session stop: nothing is gated, nothing is analog-driven, no analog value is trusted.</summary>
    public void ClearAll()
    {
        for (var slot = 0; slot < _cells.Length; slot++)
        {
            ref var cell = ref _cells[slot];
            while (true)
            {
                var current = Volatile.Read(ref cell);
                if (Commit(ref cell, current, (current & DigitalBit) | UnavailableBits))
                {
                    break;
                }
            }
        }
    }

    private static bool Commit(ref long cell, long current, long next) =>
        next == current || Interlocked.CompareExchange(ref cell, next, current) == current;

    private static KeySlot Unpack(long cell) => new(
        Digital: (cell & DigitalBit) != 0,
        Analog: BitConverter.UInt32BitsToSingle((uint)(cell & AnalogMask)),
        Gated: (cell & GatedBit) != 0,
        AnalogDriven: (cell & AnalogDrivenBit) != 0);
}
