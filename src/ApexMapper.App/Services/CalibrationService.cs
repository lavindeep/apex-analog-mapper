using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.App.Services;

/// <summary>
/// Concrete implementation of <see cref="ICalibrationService"/>.
/// Uses <see cref="IHidAnalogProbe"/> to sample normalized analog values during a capture window
/// and aggregates them into a <see cref="CalibrationSnapshot"/>.
/// </summary>
public sealed class CalibrationService : ICalibrationService
{
    private readonly IHidAnalogProbe _probe;
    private readonly Func<string> _registryPath;
    private readonly CalibrationServiceOptions _options;

    public CalibrationService(
        IHidAnalogProbe probe,
        Func<string> registryPath,
        CalibrationServiceOptions? options = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _registryPath = registryPath ?? throw new ArgumentNullException(nameof(registryPath));
        _options = options ?? CalibrationServiceOptions.Default;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Samples all mapped keys for <see cref="CalibrationServiceOptions.RestCaptureDuration"/>.
    /// Returns the <b>minimum</b> normalized value seen per key, de-normalized back to raw ADC units.
    /// </remarks>
    public Task<CalibrationSnapshot> CaptureRestAsync(Guid deviceId, CancellationToken ct)
        => CaptureAsync(_options.RestCaptureDuration, Aggregate.Min, ct);

    /// <inheritdoc/>
    public Task<CalibrationSnapshot> CaptureMaxAsync(Guid deviceId, CancellationToken ct)
        => CaptureAsync(_options.MaxCaptureDuration, Aggregate.Max, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the <b>max delta</b> (i.e. noise band) observed per key over the window.
    /// </remarks>
    public Task<CalibrationSnapshot> CaptureNoiseAsync(Guid deviceId, CancellationToken ct)
        => CaptureNoiseInternalAsync(_options.NoiseCaptureDuration, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// All-or-nothing: reads the current registry before writing; on failure, restores previous state.
    /// </remarks>
    public async Task PersistAsync(
        Guid deviceId,
        CalibrationSnapshot rest,
        CalibrationSnapshot max,
        CalibrationSnapshot noise,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(max);
        ArgumentNullException.ThrowIfNull(noise);

        var path = _registryPath();

        // Snapshot previous state for rollback.
        var previous = DeviceRegistry.Load(path);

        var keyMap = _probe.Adapter.KeyMap;
        var calibrations = new List<KeyCalibration>();

        foreach (var entry in keyMap)
        {
            var keyByte = KeyToByte(entry.ScanCode);
            if (!rest.PerKeySamples.TryGetValue(keyByte, out var restRaw)) continue;
            if (!max.PerKeySamples.TryGetValue(keyByte, out var maxRaw)) continue;
            if (!noise.PerKeySamples.TryGetValue(keyByte, out var noiseRaw)) continue;

            // De-normalize: convert ushort raw ADC back to 0..1 float.
            var span = (float)(entry.RawMax - entry.RawMin);
            var restVal = span > 0f ? (restRaw - entry.RawMin) / span : 0f;
            var maxVal = span > 0f ? (maxRaw - entry.RawMin) / span : 1f;
            var noiseBand = span > 0f ? noiseRaw / span : 0f;

            calibrations.Add(new KeyCalibration(
                Key: KeyId.FromScanCode(entry.ScanCode),
                RestValue: restVal,
                MaxPressValue: maxVal,
                NoiseBand: noiseBand));
        }

        ct.ThrowIfCancellationRequested();

        // Merge with existing calibrations (replace entries for affected keys).
        var existingById = previous.Calibrations
            .ToDictionary(c => c.Key);

        foreach (var cal in calibrations)
            existingById[cal.Key] = cal;

        var newRegistry = new DeviceRegistry(previous.SelectedDevice, existingById.Values.ToList());

        try
        {
            DeviceRegistry.Save(path, newRegistry);
        }
        catch
        {
            // Rollback: attempt to restore previous state.
            try { DeviceRegistry.Save(path, previous); }
            catch { /* best-effort rollback */ }
            throw;
        }
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private enum Aggregate { Min, Max }

    private async Task<CalibrationSnapshot> CaptureAsync(
        TimeSpan duration,
        Aggregate aggregate,
        CancellationToken ct)
    {
        var keyMap = _probe.Adapter.KeyMap;
        // Per-key accumulator: list of raw ADC ushort values.
        var samples = keyMap.ToDictionary(e => KeyToByte(e.ScanCode), _ => new List<ushort>());
        var subscriptions = new List<IDisposable>();

        try
        {
            // Subscribe to each mapped key.
            foreach (var entry in keyMap)
            {
                var keyByte = KeyToByte(entry.ScanCode);
                var e = entry; // capture
                var sub = _probe.SubscribeRaw(KeyId.FromScanCode(e.ScanCode), normalized =>
                {
                    // Convert normalized (0..1) back to raw ADC ushort.
                    var raw = NormalizedToRaw(normalized, e.RawMin, e.RawMax);
                    lock (samples)
                    {
                        if (samples.TryGetValue(keyByte, out var list))
                            list.Add(raw);
                    }
                });
                subscriptions.Add(sub);
            }

            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var sub in subscriptions) sub.Dispose();
        }

        ct.ThrowIfCancellationRequested();

        var result = new Dictionary<byte, ushort>();
        lock (samples)
        {
            foreach (var (keyByte, list) in samples)
            {
                if (list.Count == 0) continue;
                result[keyByte] = aggregate == Aggregate.Min
                    ? list.Min()
                    : list.Max();
            }
        }

        return new CalibrationSnapshot(result, DateTimeOffset.UtcNow);
    }

    private async Task<CalibrationSnapshot> CaptureNoiseInternalAsync(
        TimeSpan duration,
        CancellationToken ct)
    {
        var keyMap = _probe.Adapter.KeyMap;
        var samples = keyMap.ToDictionary(e => KeyToByte(e.ScanCode), _ => new List<ushort>());
        var subscriptions = new List<IDisposable>();

        try
        {
            foreach (var entry in keyMap)
            {
                var keyByte = KeyToByte(entry.ScanCode);
                var e = entry;
                var sub = _probe.SubscribeRaw(KeyId.FromScanCode(e.ScanCode), normalized =>
                {
                    var raw = NormalizedToRaw(normalized, e.RawMin, e.RawMax);
                    lock (samples)
                    {
                        if (samples.TryGetValue(keyByte, out var list))
                            list.Add(raw);
                    }
                });
                subscriptions.Add(sub);
            }

            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var sub in subscriptions) sub.Dispose();
        }

        ct.ThrowIfCancellationRequested();

        var result = new Dictionary<byte, ushort>();
        lock (samples)
        {
            foreach (var (keyByte, list) in samples)
            {
                if (list.Count == 0) continue;
                // Noise = max delta (max - min).
                var delta = (ushort)(list.Max() - list.Min());
                result[keyByte] = delta;
            }
        }

        return new CalibrationSnapshot(result, DateTimeOffset.UtcNow);
    }

    private static byte KeyToByte(ushort scanCode) => (byte)(scanCode & 0xFF);

    private static ushort NormalizedToRaw(float normalized, int rawMin, int rawMax)
    {
        var raw = normalized * (rawMax - rawMin) + rawMin;
        return (ushort)Math.Clamp((int)Math.Round(raw), rawMin, rawMax);
    }
}
