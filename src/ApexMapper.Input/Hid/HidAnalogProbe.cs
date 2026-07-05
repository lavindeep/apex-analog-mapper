using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Hid;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Hid;

/// <summary>
/// Composes an <see cref="IHidDevice"/>, a <see cref="HidPollLoop"/>, and a
/// <see cref="HidReportParser"/> derived from a <see cref="DeviceAdapterDescriptor"/>
/// into the complete <see cref="IHidAnalogProbe"/> the InputHost owns. Open failures
/// surface as <see cref="BackendStatus.FaultedAnalog"/> rather than escaping.
/// </summary>
public sealed class HidAnalogProbe : IHidAnalogProbe
{
    private readonly IHidDevice _device;
    private readonly DeviceAdapterDescriptor _adapter;
    private readonly KeyStateStore _store;
    private readonly int _reportLength;
    private readonly int _consecutiveFailureThreshold;
    private readonly IReadOnlyList<KeyCalibration>? _calibrations;

    private readonly object _lifecycleLock = new();
    private readonly object _statusLock = new();
    private readonly object _subLock = new();
    private readonly Dictionary<KeyId, List<Action<float>>> _rawSubscribers = new();

    private HidPollLoop? _loop;
    private IHidStream? _stream;
    private BackendStatus _status = BackendStatus.Stopped;
    private int _disposed;

    /// <param name="calibrations">
    /// Persisted per-device calibration for the selected device (from the device
    /// registry), applied over the adapter's default curves. Null or empty leaves
    /// the adapter defaults in place. The composition root (phase-4) is
    /// responsible for handing this probe the calibration list that belongs to
    /// the device it is opening.
    /// </param>
    public HidAnalogProbe(
        IHidDevice device,
        DeviceAdapterDescriptor adapter,
        KeyStateStore store,
        int reportLength,
        int consecutiveFailureThreshold = 5,
        IReadOnlyList<KeyCalibration>? calibrations = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(store);
        if (reportLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reportLength), "reportLength must be positive.");
        }
        if (consecutiveFailureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveFailureThreshold), "threshold must be positive.");
        }

        _device = device;
        _adapter = adapter;
        _store = store;
        _reportLength = reportLength;
        _consecutiveFailureThreshold = consecutiveFailureThreshold;
        _calibrations = calibrations;
    }

    public DeviceIdentity Device => _device.Identity;
    public DeviceAdapterDescriptor Adapter => _adapter;

    public BackendStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public bool IsHealthy => Status == BackendStatus.Running;

    public event EventHandler<BackendStatusChanged>? StatusChanged;

    public Task StartAsync(CancellationToken ct)
    {
        lock (_lifecycleLock)
        {
            if (_loop is not null)
            {
                return Task.CompletedTask;
            }

            ct.ThrowIfCancellationRequested();

            IHidStream stream;
            try
            {
                stream = _device.Open();
            }
            catch (Exception ex)
            {
                // Open failure surfaces as FaultedAnalog. We never let it escape into
                // the caller's task — the supervisor reacts to the event, not the throw.
                RaiseStatus(BackendStatus.FaultedAnalog, $"failed to open hid device: {ex.Message}");
                return Task.CompletedTask;
            }

            var overrides = _calibrations is { Count: > 0 }
                ? DeviceAdapterStore.ToCalibrationOverrides(_calibrations)
                : null;
            var fields = DeviceAdapterStore.ToFields(_adapter, overrides);
            var parser = new HidReportParser(fields, _adapter.ReportId);
            var loop = new HidPollLoop(
                stream,
                parser,
                _store,
                _reportLength,
                _consecutiveFailureThreshold,
                _adapter.ReportType);
            loop.StatusChanged += OnLoopStatusChanged;

            _stream = stream;
            _loop = loop;

            return loop.StartAsync(ct);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        HidPollLoop? loop;
        IHidStream? stream;
        lock (_lifecycleLock)
        {
            loop = _loop;
            stream = _stream;
            _loop = null;
            _stream = null;
        }

        if (loop is not null)
        {
            loop.StatusChanged -= OnLoopStatusChanged;
            await loop.StopAsync(ct).ConfigureAwait(false);
            await loop.DisposeAsync().ConfigureAwait(false);
        }

        stream?.Dispose();

        // If the inner loop terminated without raising a Stopped event (e.g. it was
        // never started because Open() faulted), make sure we don't get stuck at
        // Starting/Running from a previous run.
        if (Status is BackendStatus.Running or BackendStatus.Starting)
        {
            RaiseStatus(BackendStatus.Stopped, reason: null);
        }
    }

    public IDisposable SubscribeRaw(KeyId key, Action<float> onRawNormalized)
    {
        ArgumentNullException.ThrowIfNull(onRawNormalized);

        lock (_subLock)
        {
            if (!_rawSubscribers.TryGetValue(key, out var list))
            {
                list = new List<Action<float>>();
                _rawSubscribers[key] = list;
            }
            list.Add(onRawNormalized);
        }
        return new Subscription(this, key, onRawNormalized);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void OnLoopStatusChanged(object? sender, BackendStatusChanged e)
    {
        // Forward through our local cache so callers see a consistent Status property.
        RaiseStatus(e.Status, e.Reason);
    }

    private void RaiseStatus(BackendStatus next, string? reason)
    {
        lock (_statusLock)
        {
            if (_status == next)
            {
                return;
            }
            _status = next;
        }
        StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.HidAnalog, next, reason));
    }

    private void Unsubscribe(KeyId key, Action<float> handler)
    {
        lock (_subLock)
        {
            if (_rawSubscribers.TryGetValue(key, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                {
                    _rawSubscribers.Remove(key);
                }
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly HidAnalogProbe _owner;
        private readonly KeyId _key;
        private readonly Action<float> _handler;
        private int _disposed;

        public Subscription(HidAnalogProbe owner, KeyId key, Action<float> handler)
        {
            _owner = owner;
            _key = key;
            _handler = handler;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unsubscribe(_key, _handler);
            }
        }
    }
}
