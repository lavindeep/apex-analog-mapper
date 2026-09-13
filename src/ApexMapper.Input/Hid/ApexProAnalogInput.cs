using System.Collections.ObjectModel;
using System.IO;
using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Hid;

/// <summary>Serial sensor sampling with complete snapshots and release-gated recovery.</summary>
public sealed class ApexProAnalogInput : IAnalogInputSource
{
    private static readonly TimeSpan MaxSampleAge = TimeSpan.FromMilliseconds(100);
    private readonly Func<DiscoveredDevice, IHidDevice?> _resolveDevice;
    private readonly Func<DiscoveredDevice, IReadOnlyList<KeyCalibration>> _loadCalibration;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly AutoResetEvent _changed = new(false);
    private readonly HashSet<KeyId> _released = [];
    private ReadOnlyCollection<KeyId> _keys = Array.AsReadOnly<KeyId>(
        [new(0x11), new(0x1E), new(0x1F), new(0x20)]);
    private DiscoveredDevice? _device;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Snapshot? _snapshot;
    private long _generation;
    private bool _gateAll = true;
    private bool _gateAnalog;
    private bool _running;
    private bool _disposed;
    private BackendStatus _status = BackendStatus.Stopped;
    private string? _statusReason;
    private string _error = "Analog input is stopped.";

    public ApexProAnalogInput(
        Func<DiscoveredDevice, IHidDevice?> resolveDevice,
        Func<DiscoveredDevice, IReadOnlyList<KeyCalibration>> loadCalibration,
        TimeProvider? timeProvider = null)
    {
        _resolveDevice = resolveDevice ?? throw new ArgumentNullException(nameof(resolveDevice));
        _loadCalibration = loadCalibration ?? throw new ArgumentNullException(nameof(loadCalibration));
        _time = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<BackendStatusChanged>? StatusChanged;
    public BackendStatus Status { get { lock (_sync) return _status; } }
    public IReadOnlyCollection<KeyId> RequiredKeys { get { lock (_sync) return _keys; } }

    public string? ReadinessError
    {
        get
        {
            lock (_sync)
            {
                if (_keys.Count == 0) return null;
                if (_device is null) return "Select an Apex Pro TKL keyboard.";
                if (!_running) return "Analog input is stopped.";
                if (_snapshot is null) return _error;
                return IsFresh(_snapshot) ? _snapshot.CalibrationError : "Analog sensor readings are stale.";
            }
        }
    }

    public void SelectDevice(DiscoveredDevice? device)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device == device) return;
            _device = device;
            Invalidate("Waiting for the selected keyboard's sensors.");
        }
    }

    public void SetKeys(IReadOnlyCollection<KeyId> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var required = keys.Where(ApexProSensorMap.Supports).Distinct().OrderBy(key => key.ScanCode).ToArray();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_keys.SequenceEqual(required)) return;
            _keys = Array.AsReadOnly(required);
            Invalidate("Waiting for the configured keys' sensors.");
        }
    }

    public void ReloadCalibration()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Invalidate("Waiting for updated calibration and sensor readings.");
        }
    }

    public bool OwnsKey(KeyId key)
    {
        lock (_sync) return _keys.Contains(key);
    }

    public bool TryGetRaw(KeyId key, out float value)
    {
        lock (_sync)
        {
            value = 0;
            if (_snapshot is not { } snapshot || !IsFresh(snapshot)) return false;
            var index = snapshot.Keys.IndexOf(key);
            if (index < 0) return false;
            value = snapshot.Raw[index];
            return true;
        }
    }

    /// <summary>Copies a fresh snapshot on the mapping tick. No transport or calibration I/O.</summary>
    public void ApplyTo(KeyStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_sync)
        {
            if (_gateAll) store.GateHeldKeys();
            else if (_gateAnalog) store.GateHeldKeys(KeyProvenance.Analog);
            _gateAll = _gateAnalog = false;

            if (_snapshot is not { } snapshot) return;
            if (!IsFresh(snapshot))
            {
                store.GateHeldKeys(KeyProvenance.Analog);
                _snapshot = null;
                _released.Clear();
                _error = "Analog sensor readings are stale.";
                return;
            }

            for (var i = 0; i < snapshot.Keys.Count; i++)
            {
                if (snapshot.Curves[i] is not { } curve) continue;
                var key = snapshot.Keys[i];
                var depth = curve.Normalize(snapshot.Raw[i]);
                // Only a measured release may clear either held-key guard.
                if (depth == 0) _released.Add(key);
                if (_released.Contains(key)) store.Set(key, depth, KeyProvenance.Analog);
            }
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
            {
                if (!_stop!.IsCancellationRequested) return;
                await _worker.WaitAsync(ct).ConfigureAwait(false);
                _stop.Dispose();
            }
            _stop = new CancellationTokenSource();
            lock (_sync)
            {
                _running = true;
                Invalidate("Waiting for sensor readings.");
            }
            Transition(BackendStatus.Starting, null);
            var token = _stop.Token;
            _worker = Task.Factory.StartNew(() => Run(token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed && _stop is null) return;
                _running = false;
                Invalidate("Analog input is stopped.");
            }
            _stop?.Cancel();
            if (_worker is not null) await _worker.WaitAsync(ct).ConfigureAwait(false);
            Transition(BackendStatus.Stopped, null);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _stop?.Dispose();
            _stop = null;
            _worker = null;
            _changed.Dispose();
        }
        finally { _lifecycle.Release(); }
    }

    private void Run(CancellationToken ct)
    {
        WaitHandle[] wake = [ct.WaitHandle, _changed];
        while (!ct.IsCancellationRequested)
        {
            long generation;
            DiscoveredDevice? selected;
            ReadOnlyCollection<KeyId> keys;
            lock (_sync)
            {
                _changed.Reset();
                (generation, selected, keys) = (_generation, _device, _keys);
            }
            if (selected is null || keys.Count == 0)
            {
                WaitHandle.WaitAny(wake);
                continue;
            }

            try
            {
                if (selected.Identity.VendorId != 0x1038 || selected.Identity.ProductId != 0x1614)
                    throw new NotSupportedException("Analog sensors require the legacy Apex Pro TKL 1038:1614.");
                var curves = LoadCurves(selected, keys, out var calibrationError);
                if (!IsCurrent(generation, ct)) continue;
                var device = _resolveDevice(selected)
                    ?? throw new IOException("No unique sensor interface was found for the selected keyboard.");
                if (!IsCurrent(generation, ct)) continue;
                using var stream = device.Open();
                var reader = new ApexProSensorReader(device.Identity, stream);
                if (!IsCurrent(generation, ct)) continue;
                reader.VerifyFirmware();
                var groups = keys.Select(key =>
                {
                    ApexProSensorMap.TryGetSensor(key, out var group, out _);
                    return group;
                }).Distinct().Order().ToArray();
                var samples = new ushort[14];

                while (IsCurrent(generation, ct))
                {
                    var timestamp = _time.GetTimestamp();
                    var raw = new ushort[keys.Count];
                    foreach (var group in groups)
                    {
                        if (!IsCurrent(generation, ct)) break;
                        reader.ReadFilteredGroup(group, samples);
                        for (var i = 0; i < keys.Count; i++)
                        {
                            ApexProSensorMap.TryGetSensor(keys[i], out var keyGroup, out var slot);
                            if (keyGroup != group) continue;
                            if (samples[slot] > 4095) throw new IOException("Sensor counts exceeded the ADC range.");
                            raw[i] = samples[slot];
                        }
                    }
                    if (!IsCurrent(generation, ct)) break;
                    if (_time.GetElapsedTime(timestamp) >= MaxSampleAge)
                        throw new IOException("A complete sensor cycle exceeded 100 ms.");
                    lock (_sync)
                    {
                        if (_generation != generation || !_running) break;
                        if (_snapshot is { } previous && !IsFresh(previous))
                        {
                            _released.Clear();
                            _gateAnalog = true;
                        }
                        _snapshot = new Snapshot(keys, raw, curves, calibrationError, timestamp);
                    }
                    Transition(calibrationError is null ? BackendStatus.Running : BackendStatus.Degraded,
                        calibrationError, generation);
                    WaitHandle.WaitAny(wake, 5);
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    if (_generation != generation || !_running) continue;
                    _snapshot = null;
                    _released.Clear();
                    _gateAnalog = true;
                    _error = ex.Message;
                }
                Transition(BackendStatus.FaultedAnalog, ex.Message, generation);
                // Replies have no command echo. A failed exchange retires its
                // handle; a later attempt must reopen and verify firmware again.
                WaitHandle.WaitAny(wake, 1000);
            }
        }
    }

    private CalibrationCurve?[] LoadCurves(DiscoveredDevice selected, ReadOnlyCollection<KeyId> keys,
        out string? error)
    {
        var curves = new CalibrationCurve?[keys.Count];
        var seen = new bool[keys.Count];
        try
        {
            foreach (var calibration in _loadCalibration(selected))
            {
                var index = keys.IndexOf(calibration.Key);
                if (index < 0) continue;
                if (seen[index])
                {
                    curves[index] = null;
                    continue;
                }
                seen[index] = true;
                var span = MathF.Abs(calibration.MaxPressValue - calibration.RestValue);
                curves[index] = float.IsFinite(calibration.RestValue) && calibration.RestValue is >= 0 and <= 4095
                    && float.IsFinite(calibration.MaxPressValue) && calibration.MaxPressValue is >= 0 and <= 4095
                    && float.IsFinite(calibration.NoiseBand) && calibration.NoiseBand >= 0
                    && span >= 100 && calibration.NoiseBand < span
                        ? new CalibrationCurve(calibration.RestValue, calibration.MaxPressValue,
                            calibration.NoiseBand, NormalizationKind.Linear)
                        : null;
            }
            error = curves.All(curve => curve is not null) ? null : "Calibrate keys before starting.";
        }
        catch (Exception ex)
        {
            Array.Clear(curves);
            error = "Could not load analog calibration: " + ex.Message;
        }
        return curves;
    }

    private void Invalidate(string reason)
    {
        _generation++;
        _snapshot = null;
        _released.Clear();
        _gateAll = true;
        _error = reason;
        _changed.Set();
    }

    private bool IsCurrent(long generation, CancellationToken ct)
    {
        lock (_sync) return _running && _generation == generation && !ct.IsCancellationRequested;
    }

    private bool IsFresh(Snapshot snapshot) => _time.GetElapsedTime(snapshot.Timestamp) < MaxSampleAge;

    private void Transition(BackendStatus status, string? reason, long? generation = null)
    {
        lock (_sync)
        {
            if (generation is not null && (_generation != generation || !_running)) return;
            if (_status == status && _statusReason == reason) return;
            (_status, _statusReason) = (status, reason);
        }
        try { StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.HidAnalog, status, reason)); }
        catch { /* Observer failures must not stop sensor recovery. */ }
    }

    private sealed record Snapshot(ReadOnlyCollection<KeyId> Keys, ushort[] Raw,
        CalibrationCurve?[] Curves, string? CalibrationError, long Timestamp);
}
