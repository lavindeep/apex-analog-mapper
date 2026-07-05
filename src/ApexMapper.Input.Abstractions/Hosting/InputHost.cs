using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Hosting;

public sealed class InputHost : IAsyncDisposable
{
    private readonly IRawInputAdapter _rawInput;
    private readonly IHidAnalogProbe? _hidProbe;
    private readonly DeviceSelector _deviceSelector;
    private readonly SpscRingBuffer<RawKeyEvent> _ring;
    private readonly KeyStateStore _store;
    private readonly ILogSink? _log;

    private BackendStatus _digitalStatus = BackendStatus.Stopped;
    private BackendStatus _analogStatus = BackendStatus.Stopped;
    private string? _analogFallbackReason;
    private int _disposed;

    // DeviceId of the selected device's raw-input source; 0 = no selection
    // (or not yet announced by the adapter), which drops every digital event.
    // Written on the adapter/UI threads, read by Drain on the tick thread.
    private volatile int _selectedDeviceId;

    // Raw-input arrivals keyed by device path, used to bind the selected
    // identity to the DeviceId stamped on its events. Guarded by its own
    // lock: arrivals come from the adapter's pump thread while explicit
    // Select/Unselect calls resolve from the UI thread.
    private readonly Dictionary<string, (int Id, DeviceIdentity Identity)> _deviceIdsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _deviceIdsLock = new();

    public InputHost(
        IRawInputAdapter rawInput,
        IHidAnalogProbe? hidProbe,
        DeviceSelector deviceSelector,
        SpscRingBuffer<RawKeyEvent> ring,
        KeyStateStore store,
        ILogSink? log = null)
    {
        _rawInput = rawInput ?? throw new ArgumentNullException(nameof(rawInput));
        _hidProbe = hidProbe;
        _deviceSelector = deviceSelector ?? throw new ArgumentNullException(nameof(deviceSelector));
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;

        _digitalStatus = _rawInput.Status;
        _analogStatus = _hidProbe?.Status ?? BackendStatus.Stopped;

        _rawInput.StatusChanged += OnRawStatusChanged;
        _rawInput.DeviceChanged += OnRawDeviceChanged;
        _deviceSelector.Changed += OnDeviceTopologyChanged;
        if (_hidProbe is not null)
        {
            _hidProbe.StatusChanged += OnHidStatusChanged;
        }
    }

    public BackendStatus DigitalStatus => _digitalStatus;
    public BackendStatus AnalogStatus => _analogStatus;
    public string? AnalogFallbackReason => _analogFallbackReason;

    public event EventHandler<BackendStatusChanged>? StatusChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        await _rawInput.StartAsync(ct).ConfigureAwait(false);

        if (_hidProbe is not null)
        {
            try
            {
                await _hidProbe.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var reason = ex.Message;
                _analogFallbackReason = reason;
                _log?.Info("analog probe blocked: " + reason);
                RaiseHidStatus(BackendStatus.FaultedAnalog, reason);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_hidProbe is not null)
        {
            try
            {
                await _hidProbe.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Warn("hid probe stop failed: " + ex.Message);
            }
        }

        await _rawInput.StopAsync(ct).ConfigureAwait(false);
    }

    public int Drain(int maxEvents)
    {
        if (maxEvents <= 0) return 0;

        var selectedId = _selectedDeviceId;
        int drained = 0;
        while (drained < maxEvents && _ring.TryDequeue(out var ev))
        {
            drained++;

            // Only the selected device drives mapping. No selection (or a
            // selection whose id the adapter has not announced yet) drops
            // every digital event — fail-safe, integer compare only.
            if (ev.DeviceId != selectedId || selectedId == 0)
            {
                continue;
            }

            // The store enforces the held-key rule: gated keys swallow
            // pressed writes (including auto-repeat downs) and a key-up
            // clears the gate.
            var keyId = KeyId.FromScanCode(ev.ScanCode);
            _store.Set(keyId, ev.IsDown ? 1f : 0f, KeyProvenance.Digital);
        }
        return drained;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _rawInput.StatusChanged -= OnRawStatusChanged;
        _rawInput.DeviceChanged -= OnRawDeviceChanged;
        _deviceSelector.Changed -= OnDeviceTopologyChanged;
        if (_hidProbe is not null)
        {
            _hidProbe.StatusChanged -= OnHidStatusChanged;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort stop on dispose
        }

        if (_hidProbe is not null)
        {
            try { await _hidProbe.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }
        try { await _rawInput.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
    }

    private void OnRawStatusChanged(object? sender, BackendStatusChanged e)
    {
        _digitalStatus = e.Status;
        StatusChanged?.Invoke(this, e);
    }

    private void OnHidStatusChanged(object? sender, BackendStatusChanged e)
    {
        _analogStatus = e.Status;
        if (e.Status == BackendStatus.FaultedAnalog)
        {
            if (_analogFallbackReason is null)
            {
                _analogFallbackReason = e.Reason;
            }

            // A faulted probe stops reporting; sweep its stale depths so no
            // analog key keeps driving output. Deliberately fires on every
            // FaultedAnalog event, not just the first: duplicate sweeps are
            // idempotent and fail-safe (output can only drop to zero).
            _store.GateHeldKeys(KeyProvenance.Analog);
        }
        StatusChanged?.Invoke(this, e);
    }

    private void RaiseHidStatus(BackendStatus next, string? reason)
    {
        _analogStatus = next;
        StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.HidAnalog, next, reason));
    }

    private void OnRawDeviceChanged(object? sender, RawInputDeviceChanged e)
    {
        lock (_deviceIdsLock)
        {
            if (e.DevicePath.Length != 0)
            {
                if (e.Attached)
                {
                    _deviceIdsByPath[e.DevicePath] = (e.DeviceId, e.Device);
                }
                else
                {
                    _deviceIdsByPath.Remove(e.DevicePath);
                }
            }
        }

        if (!e.Attached && IsSelectedDevice(e))
        {
            // The selected device vanishing mid-press must not leave keys
            // latched. A non-selected keyboard unplugging is not a mapping
            // transition and must not zero live input.
            _store.GateHeldKeys();
        }

        try { _deviceSelector.Refresh(); }
        catch (Exception ex) { _log?.Warn("device selector refresh failed: " + ex.Message); }

        // Bind the id even when the refresh produced no topology delta — the
        // selected device may have been restored silently at Initialize and
        // only now announced by the adapter.
        UpdateSelectedDeviceId();
    }

    private void OnDeviceTopologyChanged(object? sender, DeviceTopologyChanged e)
    {
        switch (e.ChangeKind)
        {
            case DeviceTopologyChangeKind.Attached:
            case DeviceTopologyChangeKind.Selected:
            case DeviceTopologyChangeKind.Unselected:
                // Any mapping-relevant transition gates held keys so nothing
                // stays latched across it (a key held on the previously
                // selected board must release once before pressing again).
                _store.GateHeldKeys();
                UpdateSelectedDeviceId();
                break;
        }
    }

    private bool IsSelectedDevice(RawInputDeviceChanged e)
    {
        var selectedId = _selectedDeviceId;
        if (e.DeviceId != 0 && e.DeviceId == selectedId)
        {
            return true;
        }

        // Windows removals may only carry the id (the path is often gone by
        // the time we query it); fakes and legacy sources may only carry the
        // path. Either credential identifies the selected device.
        return e.DevicePath.Length != 0 &&
            _deviceSelector.SelectedDevice is { } selected &&
            string.Equals(selected.DevicePath, e.DevicePath, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSelectedDeviceId()
    {
        _selectedDeviceId = _deviceSelector.SelectedDevice is { } selected
            ? ResolveDeviceId(selected)
            : 0;
    }

    private int ResolveDeviceId(DiscoveredDevice selected)
    {
        lock (_deviceIdsLock)
        {
            if (_deviceIdsByPath.TryGetValue(selected.DevicePath, out var entry))
            {
                return entry.Id;
            }

            // Enumerator and raw-input paths for the same device can differ
            // in form; fall back to a unique VID/PID match among announced
            // keyboards. Ambiguity resolves to 0 (drop all) — fail-safe.
            var id = 0;
            foreach (var candidate in _deviceIdsByPath.Values)
            {
                if (candidate.Identity.VendorId != selected.Identity.VendorId ||
                    candidate.Identity.ProductId != selected.Identity.ProductId)
                {
                    continue;
                }

                if (id != 0)
                {
                    return 0;
                }
                id = candidate.Id;
            }
            return id;
        }
    }
}
