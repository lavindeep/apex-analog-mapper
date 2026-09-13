using ApexMapper.Core.Keys;

namespace ApexMapper.App.Services;

// Tracks physical down/up ownership; it deliberately has no device identity.
internal sealed class KeyboardSuppressionPolicy
{
    private readonly Dictionary<KeyId, KeyPress> _keys;
    private readonly KeyId[] _digitalKeys;
    private readonly int[] _captured;
    private long _generation;
    private bool _active;

    public KeyboardSuppressionPolicy(IEnumerable<KeyId> keys, IEnumerable<KeyId>? digitalKeys = null)
    {
        _digitalKeys = digitalKeys?.Distinct().ToArray() ?? [];
        _captured = new int[_digitalKeys.Length];
        _keys = keys.Distinct().ToDictionary(key => key,
            key => new KeyPress { DigitalSlot = Array.IndexOf(_digitalKeys, key) });
    }

    public long Generation => Volatile.Read(ref _generation);

    public void ApplyTo(KeyStateStore store, bool active)
    {
        for (var slot = 0; slot < _digitalKeys.Length; slot++)
            store.Set(_digitalKeys[slot], active ? Volatile.Read(ref _captured[slot]) : 0, KeyProvenance.Digital);
    }

    public void SetInitiallyHeld(KeyId key)
    {
        if (_keys.TryGetValue(key, out var press)) press.Held = press.Passed = true;
    }

    public void SetActive(bool active)
    {
        if (!active) PassHeldKeys();
        _active = active;
    }

    public bool ShouldSuppress(KeyId key, bool down, bool injected, bool shortcutHeld)
    {
        if (injected) return false;
        if (shortcutHeld || IsShortcutKey(key)) PassHeldKeys();
        if (!_keys.TryGetValue(key, out var press)) return false;
        var eligible = _active && !shortcutHeld && !IsShortcutKey(key) && key.ScanCode != 0x58;
        if (!down)
        {
            var suppress = eligible && press.Swallowed && !press.Passed;
            press.Held = press.Swallowed = press.Passed = false;
            Capture(press, false);
            return suppress;
        }
        press.Held = true;
        if (eligible && !press.Passed)
        {
            press.Swallowed = true;
            Capture(press, true);
            return true;
        }
        press.Passed = true;
        press.Swallowed = false;
        Capture(press, false);
        return false;
    }

    private void PassHeldKeys()
    {
        foreach (var press in _keys.Values)
        {
            if (press.Held) press.Passed = true;
            press.Swallowed = false;
            Capture(press, false);
        }
        Interlocked.Increment(ref _generation);
    }

    private void Capture(KeyPress press, bool down)
    {
        if (press.DigitalSlot < 0) return;
        Volatile.Write(ref _captured[press.DigitalSlot], down ? 1 : 0);
    }

    private static bool IsShortcutKey(KeyId key) => key.ScanCode is 0x1D or 0xE01D or 0x38 or 0xE038 or 0xE05B or 0xE05C;
    private sealed class KeyPress
    {
        public int DigitalSlot;
        public bool Held;
        public bool Passed;
        public bool Swallowed;
    }
}
