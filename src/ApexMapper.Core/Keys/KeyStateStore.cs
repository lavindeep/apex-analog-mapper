namespace ApexMapper.Core.Keys;

public sealed class KeyStateStore
{
    private readonly Dictionary<KeyId, KeyState>? _states;
    private readonly KeyIndex? _index;
    private readonly long[]? _cells;

    public KeyStateStore()
    {
        _states = new Dictionary<KeyId, KeyState>(capacity: 128);
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

    public void Set(KeyId key, float value, KeyProvenance source)
    {
        var clamped = value < 0f ? 0f : value > 1f ? 1f : value;

        if (_index is not null)
        {
            if (!_index.TryGetSlot(key, out var slot))
            {
                return;
            }

            Volatile.Write(ref _cells![slot], Pack(clamped, source));
            return;
        }

        _states![key] = new KeyState(clamped, source);
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
    }

    public IReadOnlyCollection<KeyId> Keys =>
        _index is not null ? _index.Keys : _states!.Keys;

    // Packed layout: bits 0..31 = float bits, bits 32..39 = provenance byte, bits 40..63 = reserved.
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
