using ApexMapper.Core.Sensors;
using ApexMapper.Windows.Hid;
using ApexMapper.Windows.Input;

namespace ApexMapper.Windows.Devices;

/// <summary>One physical SteelSeries board: every HID interface sharing a container id.</summary>
/// <param name="Known">The product id is an Apex Pro the app knows about.</param>
/// <param name="HasVendorInterface">The 0xFFC0 interface the sensor path needs is present.</param>
public sealed record KeyboardInfo(Guid ContainerId, ushort ProductId, string Name, bool Known, bool HasVendorInterface);

/// <summary>
/// Which keyboards are plugged in. The pure <see cref="Select"/> folds HID interfaces
/// into boards; <see cref="Enumerate"/> asks Windows; <see cref="Watch"/> re-enumerates
/// after the Raw Input pump reports an arrival or removal, debounced because one
/// replug raises a burst of interface events.
/// </summary>
public sealed class KeyboardDiscovery : IDisposable
{
    public const int DebounceMs = 500;

    private readonly Func<IReadOnlyList<KeyboardInfo>> _enumerate;
    private readonly Timer _debounce;
    private readonly Lock _lifetime = new();
    private RawInputPump? _pump;
    private IReadOnlyList<KeyboardInfo> _current = [];
    private string? _lastError;
    private int _handlerFaults;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread with the new list after a device change.</summary>
    public event Action<IReadOnlyList<KeyboardInfo>>? Changed;

    /// <summary>
    /// Raised on the pump thread the moment any keyboard is removed, before the debounced
    /// <see cref="Changed"/> says which. A running session pauses on it. Must be cheap; a
    /// throwing handler is counted in <see cref="HandlerFaults"/>.
    /// </summary>
    public event Action? Removing;

    public KeyboardDiscovery(Func<IReadOnlyList<KeyboardInfo>>? enumerate = null)
    {
        _enumerate = enumerate ?? Enumerate;
        _debounce = new Timer(_ => RefreshQuietly(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<KeyboardInfo> Current => Volatile.Read(ref _current);

    /// <summary>Why the last background refresh kept the previous list; null after a good one.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Exceptions thrown by <see cref="Changed"/> and <see cref="Removing"/> subscribers; they are not enumeration failures and never reach <see cref="LastError"/>.</summary>
    public int HandlerFaults => Volatile.Read(ref _handlerFaults);

    /// <summary>
    /// Folds interfaces into one entry per container id. The vendor filter is not here:
    /// enumeration asks Windows for SteelSeries devices only, and the vendor interface
    /// opens only for a known product id. A board is labelled from its known product id
    /// when any of its interfaces carries one, whatever order they enumerate in.
    /// </summary>
    public static IReadOnlyList<KeyboardInfo> Select(IEnumerable<HidInterfaceInfo> interfaces)
    {
        var boards = new Dictionary<Guid, KeyboardInfo>();
        foreach (var item in interfaces)
        {
            var model = KnownKeyboards.Find(item.ProductId);
            var name = model?.Name ?? (item.ProductName.Length > 0 ? item.ProductName : $"SteelSeries 0x{item.ProductId:X4}");
            if (!boards.TryGetValue(item.ContainerId, out var existing))
            {
                boards[item.ContainerId] = new KeyboardInfo(item.ContainerId, item.ProductId, name, model is not null, item.IsVendorInterface);
            }
            else if (!existing.Known && model is not null)
            {
                boards[item.ContainerId] = new KeyboardInfo(item.ContainerId, item.ProductId, name, true, existing.HasVendorInterface || item.IsVendorInterface);
            }
            else
            {
                boards[item.ContainerId] = existing with
                {
                    HasVendorInterface = existing.HasVendorInterface || item.IsVendorInterface,
                    Name = existing.Name.StartsWith("SteelSeries 0x", StringComparison.Ordinal) ? name : existing.Name,
                };
            }
        }
        return boards.Values.OrderBy(b => b.Name, StringComparer.Ordinal).ThenBy(b => b.ContainerId).ToArray();
    }

    /// <summary>Asks Windows now.</summary>
    public static IReadOnlyList<KeyboardInfo> Enumerate() => Select(HidVendorDevices.SteelSeriesInterfaces());

    /// <summary>Enumerates once and follows the pump's device events from then on. Calling it again moves the subscription.</summary>
    public void Watch(RawInputPump pump)
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pump?.DeviceChanged -= OnDeviceChanged;
            _pump = pump;
            pump.DeviceChanged += OnDeviceChanged;
        }
        Refresh();
    }

    /// <summary>Enumerates now, on the calling thread. Throws if enumeration does; a throwing subscriber is counted instead.</summary>
    public void Refresh() => Publish(_enumerate());

    private void Publish(IReadOnlyList<KeyboardInfo> list)
    {
        Volatile.Write(ref _current, list);
        Volatile.Write(ref _lastError, null);
        try
        {
            Changed?.Invoke(list);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_pump is not null)
            {
                _pump.DeviceChanged -= OnDeviceChanged;
            }
            _debounce.Dispose();
        }
    }

    /// <summary>Pump thread. A device change that races Dispose is ignored rather than touching a disposed timer.</summary>
    internal void OnDeviceChanged(nint device, bool arrived)
    {
        if (!arrived)
        {
            try
            {
                Removing?.Invoke();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _handlerFaults);
            }
        }
        lock (_lifetime)
        {
            if (!_disposed)
            {
                _debounce.Change(DebounceMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>Thread pool. An unhandled exception here would end the process, so enumeration failures keep the previous list and are reported through <see cref="LastError"/>.</summary>
    internal void RefreshQuietly()
    {
        IReadOnlyList<KeyboardInfo> list;
        try
        {
            list = _enumerate();
        }
        catch (Exception e)
        {
            Volatile.Write(ref _lastError, e.Message);
            return;
        }
        Publish(list);
    }
}
