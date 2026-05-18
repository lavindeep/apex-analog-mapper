namespace ApexMapper.App.Services;

/// <summary>Registers and unregisters global hotkeys that raise callbacks on the UI thread.</summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>Registers a global hotkey; throws if <paramref name="id"/> is already registered.</summary>
    void Register(string id, HotkeyGesture gesture, Action callback);

    void Unregister(string id);
    bool IsRegistered(string id);
}

public readonly record struct HotkeyGesture(
    System.Windows.Input.Key Key,
    System.Windows.Input.ModifierKeys Modifiers);
