using ApexMapper.Core.Keys;

namespace ApexMapper.Core.Engine;

public sealed class HoldGate
{
    private readonly HashSet<KeyId> _ignored = new();

    public bool IsIgnored(KeyId key) => _ignored.Contains(key);

    public void GateHeldKeys(IEnumerable<KeyId> heldKeys)
    {
        foreach (var k in heldKeys) _ignored.Add(k);
    }

    public void NotifyKeyReleased(KeyId key) => _ignored.Remove(key);

    public void ClearAll() => _ignored.Clear();
}
