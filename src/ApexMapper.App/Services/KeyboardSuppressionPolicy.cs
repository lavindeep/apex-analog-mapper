using ApexMapper.Core.Keys;

namespace ApexMapper.App.Services;

// Tracks physical down/up ownership; it deliberately has no device identity.
internal sealed class KeyboardSuppressionPolicy
{
    private readonly object _sync = new();
    private readonly Dictionary<KeyId, KeyPress> _keys;
    private readonly KeyId[] _digitalKeys;
    private readonly int[] _captured;
    private readonly bool[] _released;
    private long _generation;
    private long _storeGeneration;
    private bool _active;

    public KeyboardSuppressionPolicy(IEnumerable<KeyId> keys, IEnumerable<KeyId>? digitalKeys = null)
    {
        _digitalKeys = digitalKeys?.Distinct().ToArray() ?? [];
        _captured = new int[_digitalKeys.Length];
        _released = new bool[_digitalKeys.Length];
        _keys = keys.Distinct().ToDictionary(key => key,
            key => new KeyPress { DigitalSlot = Array.IndexOf(_digitalKeys, key) });
    }

    public long Generation => Volatile.Read(ref _generation);

    public long ApplyTo(KeyStateStore store, bool active)
    {
        lock (_sync)
        {
            var storeGeneration = store.ResetGeneration;
            if (_storeGeneration != storeGeneration)
            {
                PassHeldKeys();
                _storeGeneration = storeGeneration;
            }
            if (!active) SetActive(false);
            for (var slot = 0; slot < _digitalKeys.Length; slot++)
            {
                // Only an observed physical up can clear the store's gate.
                if (_released[slot])
                {
                    store.Set(_digitalKeys[slot], 0, KeyProvenance.Digital);
                    _released[slot] = false;
                }
                if (active && _captured[slot] != 0)
                    store.Set(_digitalKeys[slot], 1, KeyProvenance.Digital);
                else
                    store.ClearPreservingGate(_digitalKeys[slot], KeyProvenance.Digital);
            }
            // A gate racing these writes must also invalidate any queued hold.
            if (store.ResetGeneration != storeGeneration)
            {
                PassHeldKeys();
                store.GateHeldKeys(KeyProvenance.Digital);
                _storeGeneration = store.ResetGeneration;
            }
            return _generation;
        }
    }

    public void SetInitiallyHeld(KeyId key)
    {
        lock (_sync)
            if (_keys.TryGetValue(key, out var press)) press.Held = press.Passed = true;
    }

    public void SetActive(bool active)
    {
        lock (_sync)
        {
            if (!active) PassHeldKeys();
            _active = active;
        }
    }

    public bool ShouldSuppress(KeyId key, bool down, bool injected, bool shortcutHeld)
    {
        lock (_sync)
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
                if (press.DigitalSlot >= 0) _released[press.DigitalSlot] = true;
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
    }

    private void PassHeldKeys()
    {
        foreach (var press in _keys.Values)
        {
            if (press.Held)
            {
                press.Passed = true;
                if (press.DigitalSlot >= 0) _released[press.DigitalSlot] = false;
            }
            press.Swallowed = false;
            Capture(press, false);
        }
        Interlocked.Increment(ref _generation);
    }

    private void Capture(KeyPress press, bool down)
    {
        if (press.DigitalSlot >= 0) _captured[press.DigitalSlot] = down ? 1 : 0;
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
