using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ApexMapper.App.Services;
using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Persistence.Devices;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Calibration;

// ---------------------------------------------------------------------------
// Fake IHidAnalogProbe
// ---------------------------------------------------------------------------

/// <summary>
/// Fake probe: delivers preconfigured per-key samples synchronously at the moment
/// <see cref="SubscribeRaw"/> is called. This avoids timing races with Task.Delay.
/// </summary>
internal sealed class FakeHidAnalogProbe : IHidAnalogProbe
{
    private readonly DeviceAdapterDescriptor _adapter;

    // Samples to push immediately on SubscribeRaw (per keyByte).
    private readonly Dictionary<byte, List<float>> _immediateSamples = new();

    public FakeHidAnalogProbe(DeviceAdapterDescriptor adapter)
    {
        _adapter = adapter;
    }

    // IInputBackend
    public BackendStatus Status => BackendStatus.Running;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public event EventHandler<BackendStatusChanged>? StatusChanged
    {
        add { }
        remove { }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // IHidAnalogProbe
    public DeviceIdentity Device => new(0x1038, 0x1610, null, "SteelSeries", "Apex Pro");
    public DeviceAdapterDescriptor Adapter => _adapter;
    public bool IsHealthy => true;

    public IDisposable SubscribeRaw(KeyId key, Action<float> onRawNormalized)
    {
        var keyByte = (byte)(key.ScanCode & 0xFF);

        // Deliver all preconfigured samples immediately (synchronous, before Task.Delay).
        if (_immediateSamples.TryGetValue(keyByte, out var samples))
        {
            foreach (var s in samples)
                onRawNormalized(s);
        }

        // Return a no-op disposable — no ongoing subscription needed in tests.
        return NoopDisposable.Instance;
    }

    /// <summary>Queue samples to be delivered synchronously when the subscriber subscribes.</summary>
    public FakeHidAnalogProbe WithSamples(byte keyByte, params float[] normalizedValues)
    {
        if (!_immediateSamples.TryGetValue(keyByte, out var list))
        {
            list = new List<float>();
            _immediateSamples[keyByte] = list;
        }
        list.AddRange(normalizedValues);
        return this;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

// ---------------------------------------------------------------------------
// Helpers / Tests
// ---------------------------------------------------------------------------

public sealed class CalibrationServiceTests
{
    // -----------------------------------------------------------------------
    // Adapter / builder helpers
    // -----------------------------------------------------------------------

    private static DeviceAdapterDescriptor MakeAdapter(params (ushort ScanCode, int RawMin, int RawMax)[] keys)
    {
        var entries = keys.Length > 0
            ? keys.Select(k => new KeyMapEntry(k.ScanCode, ByteOffset: 1, BitWidth: 8,
                  Normalization: NormalizationKind.Linear, RawMin: k.RawMin, RawMax: k.RawMax))
                .ToArray()
            : new KeyMapEntry[]
              {
                  new(ScanCode: 0x04, ByteOffset: 1, BitWidth: 8,
                      Normalization: NormalizationKind.Linear, RawMin: 0, RawMax: 255),
                  new(ScanCode: 0x05, ByteOffset: 2, BitWidth: 8,
                      Normalization: NormalizationKind.Linear, RawMin: 0, RawMax: 255),
              };

        return new DeviceAdapterDescriptor(
            SchemaVersion: "1",
            Id: "fake",
            DisplayName: "Fake Adapter",
            Match: new DeviceMatch(0x1038, 0x1610, null, null, null, null),
            InterfaceSelector: new InterfaceSelector(null, null, null),
            ReportId: 0x01,
            ReportType: HidReportType.Input,
            KeyMap: entries,
            NoiseFloor: 0.005f,
            RestWindow: 0.02f,
            ProbeHandshake: null,
            Capabilities: new AdapterCapabilities(Analog: true, PerKeyTravel: true));
    }

    private static CalibrationServiceOptions InstantOptions => new(
        RestCaptureDuration: TimeSpan.FromMilliseconds(5),
        MaxCaptureDuration: TimeSpan.FromMilliseconds(5),
        NoiseCaptureDuration: TimeSpan.FromMilliseconds(5),
        SamplesPerSecond: 100);

    private static (FakeHidAnalogProbe Probe, CalibrationService Service, string Path) Build(
        FakeHidAnalogProbe? probe = null,
        CalibrationServiceOptions? options = null)
    {
        var adapter = MakeAdapter();
        probe ??= new FakeHidAnalogProbe(adapter);
        var tmp = Path.Combine(Path.GetTempPath(), $"TestRegistry_{Guid.NewGuid():N}.json");
        var service = new CalibrationService(probe, () => tmp, options ?? InstantOptions);
        return (probe, service, tmp);
    }

    // -----------------------------------------------------------------------
    // Test 1: CaptureRestAsync aggregates min over window
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CaptureRestAsync_aggregates_min_over_window()
    {
        // Key 0x04: samples are 10/255, 200/255 → min raw = 10
        // Key 0x05: samples are 50/255, 180/255 → min raw = 50
        var adapter = MakeAdapter();
        var probe = new FakeHidAnalogProbe(adapter)
            .WithSamples(0x04, 10f / 255f, 200f / 255f)
            .WithSamples(0x05, 50f / 255f, 180f / 255f);

        var (_, service, _) = Build(probe);
        var snapshot = await service.CaptureRestAsync(Guid.NewGuid(), CancellationToken.None);

        snapshot.PerKeySamples.Should().ContainKey(0x04);
        snapshot.PerKeySamples.Should().ContainKey(0x05);
        snapshot.PerKeySamples[0x04].Should().Be(10);
        snapshot.PerKeySamples[0x05].Should().Be(50);
    }

    // -----------------------------------------------------------------------
    // Test 2: CaptureMaxAsync aggregates max over window
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CaptureMaxAsync_aggregates_max_over_window()
    {
        var adapter = MakeAdapter();
        var probe = new FakeHidAnalogProbe(adapter)
            .WithSamples(0x04, 10f / 255f, 200f / 255f)
            .WithSamples(0x05, 50f / 255f, 180f / 255f);

        var (_, service, _) = Build(probe);
        var snapshot = await service.CaptureMaxAsync(Guid.NewGuid(), CancellationToken.None);

        snapshot.PerKeySamples[0x04].Should().Be(200);
        snapshot.PerKeySamples[0x05].Should().Be(180);
    }

    // -----------------------------------------------------------------------
    // Test 3: CaptureNoiseAsync returns per-key jitter (max delta)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CaptureNoiseAsync_returns_per_key_jitter()
    {
        // Key 0x04: 98, 100, 102 → delta = 102 - 98 = 4
        // Key 0x05: 125, 128, 135 → delta = 135 - 125 = 10
        var adapter = MakeAdapter();
        var probe = new FakeHidAnalogProbe(adapter)
            .WithSamples(0x04, 98f / 255f, 100f / 255f, 102f / 255f)
            .WithSamples(0x05, 125f / 255f, 128f / 255f, 135f / 255f);

        var (_, service, _) = Build(probe);
        var snapshot = await service.CaptureNoiseAsync(Guid.NewGuid(), CancellationToken.None);

        snapshot.PerKeySamples[0x04].Should().Be(4);
        snapshot.PerKeySamples[0x05].Should().Be(10);
    }

    // -----------------------------------------------------------------------
    // Test 4: PersistAsync writes to registry atomically (rollback on failure)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PersistAsync_writes_to_registry_atomically()
    {
        var (_, service, tmpPath) = Build();

        try
        {
            var rest = new CalibrationSnapshot(
                new Dictionary<byte, ushort> { [0x04] = 10, [0x05] = 50 },
                DateTimeOffset.UtcNow);
            var max = new CalibrationSnapshot(
                new Dictionary<byte, ushort> { [0x04] = 200, [0x05] = 180 },
                DateTimeOffset.UtcNow);
            var noise = new CalibrationSnapshot(
                new Dictionary<byte, ushort> { [0x04] = 4, [0x05] = 10 },
                DateTimeOffset.UtcNow);

            await service.PersistAsync(Guid.NewGuid(), rest, max, noise, CancellationToken.None);

            var loaded = DeviceRegistry.Load(tmpPath);
            loaded.Calibrations.Should().HaveCount(2);

            var key04 = KeyId.FromScanCode(0x04);
            var key05 = KeyId.FromScanCode(0x05);
            loaded.Calibrations.Should().Contain(c => c.Key == key04);
            loaded.Calibrations.Should().Contain(c => c.Key == key05);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    // -----------------------------------------------------------------------
    // Test 5: PersistAsync round-trips via real registry (reload and verify)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PersistAsync_round_trips_via_real_registry()
    {
        var (_, service, tmpPath) = Build();

        try
        {
            var rest = new CalibrationSnapshot(
                new Dictionary<byte, ushort> { [0x04] = 5, [0x05] = 30 },
                DateTimeOffset.UtcNow);
            var max = new CalibrationSnapshot(
                new Dictionary<byte, ushort> { [0x04] = 250, [0x05] = 220 },
                DateTimeOffset.UtcNow);
            var noise = new CalibrationSnapshot(
                new Dictionary<byte, ushort> { [0x04] = 2, [0x05] = 3 },
                DateTimeOffset.UtcNow);

            await service.PersistAsync(Guid.NewGuid(), rest, max, noise, CancellationToken.None);

            var loaded = DeviceRegistry.Load(tmpPath);
            loaded.Calibrations.Should().HaveCount(2);

            var key04Cal = loaded.Calibrations.Single(c => c.Key == KeyId.FromScanCode(0x04));
            var key05Cal = loaded.Calibrations.Single(c => c.Key == KeyId.FromScanCode(0x05));

            // Persisted values are raw ADC units (consumed directly as curve
            // endpoints), so they equal the captured raw samples verbatim.
            key04Cal.RestValue.Should().BeApproximately(5f, 0.001f);
            key04Cal.MaxPressValue.Should().BeApproximately(250f, 0.001f);
            key04Cal.NoiseBand.Should().BeApproximately(2f, 0.001f);

            key05Cal.RestValue.Should().BeApproximately(30f, 0.001f);
            key05Cal.MaxPressValue.Should().BeApproximately(220f, 0.001f);
            key05Cal.NoiseBand.Should().BeApproximately(3f, 0.001f);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    // -----------------------------------------------------------------------
    // Test 6: Capture respects cancellation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Capture_respects_cancellation()
    {
        // Long duration so cancellation fires first.
        var longOptions = new CalibrationServiceOptions(
            RestCaptureDuration: TimeSpan.FromSeconds(30),
            MaxCaptureDuration: TimeSpan.FromSeconds(30),
            NoiseCaptureDuration: TimeSpan.FromSeconds(30),
            SamplesPerSecond: 100);

        var (_, service, tmpPath) = Build(options: longOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Func<Task> act = () => service.CaptureRestAsync(Guid.NewGuid(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // No registry file should have been created.
        File.Exists(tmpPath).Should().BeFalse();
    }
}
