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

    // True once the input ring has reported at least one dropped event and we
    // have logged it. Touched only on the Drain (tick) thread.
    private bool _ringOverflowLogged;

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

    // Number of raw-input events the ring has dropped because it was full — a
    // sign the tick loop is not draining fast enough. Surfaces the otherwise
    // invisible SpscRingBuffer.DroppedCount for diagnostics/status.
    public long DroppedInputEvents => _ring.DroppedCount;

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

        // Surface a ring overflow once: dropped digital events mean the tick
        // loop fell behind and some key transitions were lost.
        if (!_ringOverflowLogged && _ring.DroppedCount > 0)
        {
            _ringOverflowLogged = true;
            _log?.Warn($"input ring overflow: dropped {_ring.DroppedCount} raw event(s)");
        }

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

        // A selection change that landed mid-loop published its new id and
        // then swept — but this loop may have admitted an event under the
        // stale snapshot AFTER that sweep. Re-sweep on change: an event
        // admitted under the stale id is either pre-sweep (zeroed by the
        // change's sweep) or post-sweep (caught here), and a pressed write
        // cannot survive both sweeps because gated keys swallow presses.
        // Cheap and idempotent; runs only on the rare mid-drain change.
        if (_selectedDeviceId != selectedId)
        {
            _store.GateHeldKeys();
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
            if (e.Attached)
            {
                if (e.DevicePath.Length != 0)
                {
                    _deviceIdsByPath[e.DevicePath] = (e.DeviceId, e.Device);
                }
            }
            else
            {
                if (e.DevicePath.Length != 0)
                {
                    _deviceIdsByPath.Remove(e.DevicePath);
                }

                // Windows removals often carry only the id — the path is
                // unretrievable by removal time. Purge by id as well, or a
                // stale path entry keeps resolving the vanished unit's id
                // and its orphaned events stay admissible.
                if (e.DeviceId != 0)
                {
                    foreach (var entry in _deviceIdsByPath)
                    {
                        if (entry.Value.Id == e.DeviceId)
                        {
                            _deviceIdsByPath.Remove(entry.Key);
                        }
                    }
                }
            }
        }

        if (!e.Attached && IsSelectedDevice(e))
        {
            // Fail-safe: stop admitting the vanished unit's events NOW. The
            // HID enumerator can lag the raw-input removal, so the Refresh
            // below may keep the selection alive; a later Refresh or attach
            // reconciles and re-publishes the id. Publish the drop before
            // sweeping so no event admitted after the sweep can re-latch.
            _selectedDeviceId = 0;

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
                //
                // Ordering contract with Drain: publish the new id FIRST,
                // then sweep. A Drain in flight with the old snapshot can
                // admit events until it observes the new id; sweeping after
                // the publish guarantees any event it admitted before the
                // sweep is zeroed, and anything it admits after is caught by
                // Drain's own post-loop re-sweep. Sweep-before-publish would
                // leave a window where a stale-id down lands after the sweep
                // and latches.
                UpdateSelectedDeviceId();
                _store.GateHeldKeys();
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
        // Recompute-and-publish is serialized under the map lock: this runs
        // on the adapter's pump thread and on the UI thread, and without the
        // lock a pump recompute that read a still-live selection could
        // overwrite the 0 a concurrent Unselect just published. Off the hot
        // path — Drain only reads the volatile field.
        lock (_deviceIdsLock)
        {
            _selectedDeviceId = _deviceSelector.SelectedDevice is { } selected
                ? ResolveDeviceId(selected)
                : 0;
        }
    }

    // Caller must hold _deviceIdsLock.
    private int ResolveDeviceId(DiscoveredDevice selected)
    {
        if (_deviceIdsByPath.TryGetValue(selected.DevicePath, out var entry))
        {
            return entry.Id;
        }

        // Enumerator and raw-input paths for the same device can differ in
        // form; fall back to a VID/PID match — but only when that VID/PID is
        // provably a single physical unit: exactly one candidate among the
        // raw-input arrivals AND exactly one among the enumerator's
        // discovered devices. With identical twins attached, the selected
        // unit's own entry may be the missing one, so a "unique" map match
        // could bind its sibling. Any ambiguity resolves to 0 (drop all)
        // until topology settles — fail-safe.
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

        if (id == 0)
        {
            return 0;
        }

        var discoveredMatches = 0;
        foreach (var device in _deviceSelector.Discovered)
        {
            if (device.Identity.VendorId == selected.Identity.VendorId &&
                device.Identity.ProductId == selected.Identity.ProductId)
            {
                discoveredMatches++;
            }
        }
        return discoveredMatches == 1 ? id : 0;
    }
}
