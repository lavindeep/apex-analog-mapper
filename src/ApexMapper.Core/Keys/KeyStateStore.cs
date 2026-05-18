namespace ApexMapper.Core.Keys;

public sealed class KeyStateStore
{
    private readonly Dictionary<KeyId, KeyState> _states = new(capacity: 128);

    public KeyState Get(KeyId key) => _states.TryGetValue(key, out var s) ? s : KeyState.Rest;

    public void Set(KeyId key, float value, KeyProvenance source)
    {
        var clamped = value < 0f ? 0f : value > 1f ? 1f : value;
        _states[key] = new KeyState(clamped, source);
    }

    public void Reset()
    {
        foreach (var k in _states.Keys.ToArray())
        {
            _states[k] = KeyState.Rest;
        }
    }

    public IReadOnlyCollection<KeyId> Keys => _states.Keys;
}
