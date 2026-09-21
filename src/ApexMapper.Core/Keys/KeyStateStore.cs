namespace ApexMapper.Core.Keys;

/// <summary>What the store knows about one key at one instant.</summary>
/// <param name="Digital">The hook's last observation: key is down.</param>
/// <param name="Analog">Sensor depth in 0..1, or NaN when the sensor reading is unavailable.</param>
/// <param name="Gated">Physical state was unknown at some transition and no release has been seen since.</param>
/// <param name="AnalogDriven">The active profile drives this key from the sensor.</param>
public readonly record struct KeySlot(bool Digital, float Analog, bool Gated, bool AnalogDriven)
{
    /// <summary>
    /// A fresh reading inside the calibration noise band normalises to exactly zero.
    /// <see cref="Calibration.Normalizer"/> is the only producer of analog values and
    /// returns a literal zero inside the band, so exact comparison is right here.
    /// </summary>
    public bool AnalogAtRest => Analog == 0f;
}

/// <summary>
/// One packed cell per scan code slot, written by the hook thread (digital) and the
/// engine thread (analog), read by the engine. Each cell holds both values so digital
/// fallback can read the hook's state at the instant the sensor drops out.
///
/// The gate bit marks a key whose physical state became unknown (session start,
/// return from alt-tab, hook install, keyboard reconnect). While gated the key
/// contributes nothing. Only an observed release clears it. While the sensor reading
/// for the key is available, only a reading at rest clears it: with rapid trigger a
/// hook key-up means the key rose a fraction of a millimetre, not that it was
/// released. While the reading is unavailable (sensor stale, faulted, or not yet
/// read) the hook is the only evidence there is, and any hook event other than an
/// auto-repeat clears the gate, exactly as for a key the profile drives digitally.
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

    /// <summary>
    /// Hook thread. A key-up, or a key-down from up, clears the gate unless the sensor
    /// reading for the key is available; an auto-repeat (down while down) never does.
    /// </summary>
    public void SetDigital(int slot, bool down)
    {
        ref var cell = ref _cells[slot];
        while (true)
        {
            var current = Volatile.Read(ref cell);
            var next = down ? current | DigitalBit : current & ~DigitalBit;
            var repeat = down && (current & DigitalBit) != 0;
            if (!repeat && !AnalogAvailable(next))
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

    /// <summary>Called by the mapper at construction, before any gating.</summary>
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

    /// <summary>Drops every slot's analog-driven flag, so a new mapper starts from a clean set.</summary>
    public void ClearAnalogDriven()
    {
        for (var slot = 0; slot < _cells.Length; slot++)
        {
            ref var cell = ref _cells[slot];
            while (true)
            {
                var current = Volatile.Read(ref cell);
                if (Commit(ref cell, current, current & ~AnalogDrivenBit))
                {
                    break;
                }
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
    /// reading, or the next hook event while the reading is unavailable, clears them)
    /// and every digital key currently down. A digital key that is up is known to be
    /// up and stays ungated, otherwise it could never clear. A key pressed while this
    /// sweep is running may land after its slot was visited and stay ungated; that is
    /// a genuine new press and is meant to count.
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

    private static bool AnalogAvailable(long cell) =>
        (cell & AnalogDrivenBit) != 0 && (cell & AnalogMask) != UnavailableBits;

    private static bool Commit(ref long cell, long current, long next) =>
        next == current || Interlocked.CompareExchange(ref cell, next, current) == current;

    private static KeySlot Unpack(long cell) => new(
        Digital: (cell & DigitalBit) != 0,
        Analog: BitConverter.UInt32BitsToSingle((uint)(cell & AnalogMask)),
        Gated: (cell & GatedBit) != 0,
        AnalogDriven: (cell & AnalogDrivenBit) != 0);
}
