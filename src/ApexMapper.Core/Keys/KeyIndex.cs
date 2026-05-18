namespace ApexMapper.Core.Keys;

public sealed class KeyIndex
{
    private readonly KeyId[] _keysBySlot;
    private readonly Dictionary<KeyId, int> _slotByKey;

    public KeyIndex(IEnumerable<KeyId> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var slotByKey = new Dictionary<KeyId, int>();
        foreach (var key in keys)
        {
            if (!slotByKey.ContainsKey(key))
            {
                slotByKey.Add(key, slotByKey.Count);
            }
        }

        var keysBySlot = new KeyId[slotByKey.Count];
        foreach (var pair in slotByKey)
        {
            keysBySlot[pair.Value] = pair.Key;
        }

        _slotByKey = slotByKey;
        _keysBySlot = keysBySlot;
    }

    public int Count => _keysBySlot.Length;

    public IReadOnlyList<KeyId> Keys => _keysBySlot;

    public bool TryGetSlot(KeyId key, out int slot)
    {
        if (_slotByKey.TryGetValue(key, out slot))
        {
            return true;
        }

        slot = -1;
        return false;
    }

    public KeyId KeyAt(int slot)
    {
        if ((uint)slot >= (uint)_keysBySlot.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot must be in [0, {_keysBySlot.Length}).");
        }

        return _keysBySlot[slot];
    }
}
