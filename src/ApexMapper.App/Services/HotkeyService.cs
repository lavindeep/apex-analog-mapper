using System.Collections.Generic;
using System.Windows.Input;
using NHotkey;
using NHotkey.Wpf;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete <see cref="IHotkeyService"/> backed by NHotkey.Wpf.
/// Must be created and called on the WPF UI thread.
/// NHotkey dispatches callbacks on the WPF UI thread — callers that wish to
/// avoid blocking the dispatcher should marshal work to a background thread.
/// </summary>
public sealed class HotkeyService : IHotkeyService
{
    // id → EventHandler delegate so we can unregister without closing over managed state.
    private readonly Dictionary<string, EventHandler<HotkeyEventArgs>> _handlers = new();

    public void Register(string id, HotkeyGesture gesture, Action callback)
    {
        if (_handlers.ContainsKey(id))
            throw new InvalidOperationException($"Hotkey id '{id}' is already registered.");

        EventHandler<HotkeyEventArgs> handler = (_, _) => callback();
        _handlers[id] = handler;

        HotkeyManager.Current.AddOrReplace(
            id,
            MapKey(gesture.Key),
            MapModifiers(gesture.Modifiers),
            handler);
    }

    public void Unregister(string id)
    {
        if (!_handlers.Remove(id)) return;
        HotkeyManager.Current.Remove(id);
    }

    public bool IsRegistered(string id) => _handlers.ContainsKey(id);

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
            HotkeyManager.Current.Remove(id);
        _handlers.Clear();
    }

    // ---------------------------------------------------------------------------
    // Gesture → NHotkey conversion (pure, deterministic — can be tested without WPF)
    // ---------------------------------------------------------------------------

    internal static System.Windows.Input.Key MapKey(System.Windows.Input.Key key) => key;

    internal static System.Windows.Input.ModifierKeys MapModifiers(System.Windows.Input.ModifierKeys modifiers) => modifiers;
}
