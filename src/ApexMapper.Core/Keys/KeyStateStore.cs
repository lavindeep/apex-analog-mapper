namespace ApexMapper.Core.Keys;

/// <summary>
/// Holds the live depth of every key and enforces the held-key rule: after a
/// gate transition (device attach/detach, backend fault, mode or profile
/// switch), keys that were held are zeroed and ignored until released once.
/// While a key is gated, any pressed write (value &gt; 0) from any source is
/// ignored; a write of exactly 0 — a digital key-up or a fully-released
/// analog report — clears the gate and the next press works normally.
/// </summary>
public sealed class KeyStateStore
{
    private readonly Dictionary<KeyId, KeyState>? _states;
    private readonly HashSet<KeyId>? _gated;
    private readonly KeyIndex? _index;
    private readonly long[]? _cells;

    public KeyStateStore()
    {
        _states = new Dictionary<KeyId, KeyState>(capacity: 128);
        _gated = new HashSet<KeyId>();
    }

    public KeyStateStore(KeyIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _cells = new long[index.Count];
    }

    public KeyState Get(KeyId key)
    {
        if (_index is not null)
        {
            if (!_index.TryGetSlot(key, out var slot))
            {
                return KeyState.Rest;
            }

            var packed = Volatile.Read(ref _cells![slot]);
            return Unpack(packed);
        }

        return _states!.TryGetValue(key, out var s) ? s : KeyState.Rest;
    }

    /// <summary>
    /// Writes a key's depth. Ignored while the key is gated and the value is
    /// pressed (&gt; 0); a value of exactly 0 clears the gate.
    /// </summary>
    public void Set(KeyId key, float value, KeyProvenance source)
    {
        var clamped = value < 0f ? 0f : value > 1f ? 1f : value;

        if (_index is not null)
        {
            if (!_index.TryGetSlot(key, out var slot))
            {
                return;
            }

            ref var cell = ref _cells![slot];
            while (true)
            {
                var current = Volatile.Read(ref cell);
                if ((current & GateBit) != 0 && clamped > 0f)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref cell, Pack(clamped, source), current) == current)
                {
                    return;
                }
            }
        }

        if (clamped > 0f)
        {
            if (_gated!.Contains(key))
            {
                return;
            }
        }
        else
        {
            _gated!.Remove(key);
        }

        _states![key] = new KeyState(clamped, source);
    }

    /// <summary>Gates and zeroes every key whose current value is &gt; 0.</summary>
    public void GateHeldKeys() => GateHeldKeysCore(source: null);

    /// <summary>Gates and zeroes every held key whose provenance matches <paramref name="source"/>.</summary>
    public void GateHeldKeys(KeyProvenance source) => GateHeldKeysCore(source);

    public bool IsGated(KeyId key)
    {
        if (_index is not null)
        {
            return _index.TryGetSlot(key, out var slot)
                && (Volatile.Read(ref _cells![slot]) & GateBit) != 0;
        }

        return _gated!.Contains(key);
    }

    public void Reset()
    {
        if (_cells is not null)
        {
            Array.Clear(_cells, 0, _cells.Length);
            return;
        }

        var dict = _states!;
        foreach (var k in dict.Keys.ToArray())
        {
            dict[k] = KeyState.Rest;
        }
        _gated!.Clear();
    }

    public IReadOnlyCollection<KeyId> Keys =>
        _index is not null ? _index.Keys : _states!.Keys;

    private void GateHeldKeysCore(KeyProvenance? source)
    {
        if (_cells is not null)
        {
            for (var slot = 0; slot < _cells.Length; slot++)
            {
                ref var cell = ref _cells[slot];
                while (true)
                {
                    var current = Volatile.Read(ref cell);
                    var state = Unpack(current);
                    if (state.Value <= 0f || (source is not null && state.Source != source.Value))
                    {
                        break;
                    }

                    var next = GateBit | ((long)(byte)state.Source << 32);
                    if (Interlocked.CompareExchange(ref cell, next, current) == current)
                    {
                        break;
                    }
                }
            }

            return;
        }

        var dict = _states!;
        foreach (var k in dict.Keys.ToArray())
        {
            var state = dict[k];
            if (state.Value <= 0f || (source is not null && state.Source != source.Value))
            {
                continue;
            }

            _gated!.Add(k);
            dict[k] = new KeyState(0f, state.Source);
        }
    }

    // Packed layout: bits 0..31 = float bits, bits 32..39 = provenance byte,
    // bit 40 = held-key gate flag, bits 41..63 = reserved.
    private const long GateBit = 1L << 40;

    private static long Pack(float value, KeyProvenance source)
    {
        var bits = (uint)BitConverter.SingleToInt32Bits(value);
        return (long)bits | ((long)(byte)source << 32);
    }

    private static KeyState Unpack(long packed)
    {
        var value = BitConverter.Int32BitsToSingle((int)(uint)packed);
        var source = (KeyProvenance)(byte)(packed >> 32);
        return new KeyState(value, source);
    }
}
