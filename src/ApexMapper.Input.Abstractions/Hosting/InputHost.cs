using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Pipeline;

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

        int drained = 0;
        while (drained < maxEvents && _ring.TryDequeue(out var ev))
        {
            // The store enforces the held-key rule: gated keys swallow
            // pressed writes (including auto-repeat downs) and a key-up
            // clears the gate.
            var keyId = KeyId.FromScanCode(ev.ScanCode);
            _store.Set(keyId, ev.IsDown ? 1f : 0f, KeyProvenance.Digital);
            drained++;
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
        if (!e.Attached)
        {
            // Unplug mid-press must not leave keys latched. Device-identity
            // filtering is a separate upcoming change; until then any keyboard
            // detach sweeps all held keys — a safe over-approximation.
            _store.GateHeldKeys();
        }

        try { _deviceSelector.Refresh(); }
        catch (Exception ex) { _log?.Warn("device selector refresh failed: " + ex.Message); }
    }

    private void OnDeviceTopologyChanged(object? sender, DeviceTopologyChanged e)
    {
        if (e.ChangeKind != DeviceTopologyChangeKind.Attached) return;

        _store.GateHeldKeys();
    }
}
