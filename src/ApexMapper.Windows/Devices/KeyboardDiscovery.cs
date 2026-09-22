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
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread with the new list after a device change.</summary>
    public event Action<IReadOnlyList<KeyboardInfo>>? Changed;

    public KeyboardDiscovery(Func<IReadOnlyList<KeyboardInfo>>? enumerate = null)
    {
        _enumerate = enumerate ?? Enumerate;
        _debounce = new Timer(_ => RefreshQuietly(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<KeyboardInfo> Current => Volatile.Read(ref _current);

    /// <summary>Why the last background refresh kept the previous list; null after a good one.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Folds interfaces into one entry per container id, SteelSeries only.</summary>
    public static IReadOnlyList<KeyboardInfo> Select(IEnumerable<HidInterfaceInfo> interfaces)
    {
        var boards = new Dictionary<Guid, KeyboardInfo>();
        foreach (var item in interfaces)
        {
            var model = KnownKeyboards.Find(item.ProductId);
            var name = model?.Name ?? (item.ProductName.Length > 0 ? item.ProductName : $"SteelSeries 0x{item.ProductId:X4}");
            if (boards.TryGetValue(item.ContainerId, out var existing))
            {
                boards[item.ContainerId] = existing with
                {
                    HasVendorInterface = existing.HasVendorInterface || item.IsVendorInterface,
                    Name = existing.Name.StartsWith("SteelSeries 0x", StringComparison.Ordinal) ? name : existing.Name,
                };
            }
            else
            {
                boards[item.ContainerId] = new KeyboardInfo(item.ContainerId, item.ProductId, name, model is not null, item.IsVendorInterface);
            }
        }
        return boards.Values.OrderBy(b => b.Name, StringComparer.Ordinal).ThenBy(b => b.ContainerId).ToArray();
    }

    /// <summary>Asks Windows now.</summary>
    public static IReadOnlyList<KeyboardInfo> Enumerate() => Select(HidVendorDevices.SteelSeriesInterfaces());

    /// <summary>Enumerates once and follows the pump's device events from then on.</summary>
    public void Watch(RawInputPump pump)
    {
        _pump = pump;
        pump.DeviceChanged += OnDeviceChanged;
        Refresh();
    }

    /// <summary>Enumerates now, on the calling thread. Throws if enumeration does.</summary>
    public void Refresh()
    {
        var list = _enumerate();
        Volatile.Write(ref _current, list);
        Volatile.Write(ref _lastError, null);
        Changed?.Invoke(list);
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
        try
        {
            Refresh();
        }
        catch (Exception e)
        {
            Volatile.Write(ref _lastError, e.Message);
        }
    }
}
