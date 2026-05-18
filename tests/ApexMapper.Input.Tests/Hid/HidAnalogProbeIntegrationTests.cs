using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Input.Hid;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Tests.Hid;

[Trait("os", "windows")]
public class HidAnalogProbeIntegrationTests
{
    private const int ReportLength = 1;

    private static DeviceAdapterDescriptor MakeDescriptorForKey(ushort scanCode) =>
        new(
            SchemaVersion: "1",
            Id: "test.fake.v1",
            DisplayName: "Test Fake",
            Match: new DeviceMatch(0x1234, 0x5678, UsagePage: null, ProductRegex: null, ManufacturerRegex: null, FirmwareVersion: null),
            InterfaceSelector: new InterfaceSelector(UsagePage: null, UsageId: null, CollectionPath: null),
            ReportId: 0,
            ReportType: HidReportType.Input,
            KeyMap: new[]
            {
                new KeyMapEntry(
                    ScanCode: scanCode,
                    ByteOffset: 0,
                    BitWidth: 8,
                    Normalization: NormalizationKind.Linear,
                    RawMin: 0,
                    RawMax: 255),
            },
            NoiseFloor: 0f,
            RestWindow: 0.05f,
            ProbeHandshake: null,
            Capabilities: new AdapterCapabilities(Analog: true, PerKeyTravel: true));

    private static DeviceIdentity MakeIdentity() =>
        new(0x1234, 0x5678, SerialNumber: "SN-001", ManufacturerName: "Fake Co", ProductName: "FakePad");

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("predicate never became true");
    }

    [Fact]
    public async Task Starts_runs_and_writes_analog_values_into_store()
    {
        const ushort scanCode = 0x11;
        var key = KeyId.FromScanCode(scanCode);
        var store = new KeyStateStore(new KeyIndex(new[] { key }));
        var descriptor = MakeDescriptorForKey(scanCode);
        var fakeStream = new InlineFakeHidStream(new[]
        {
            new byte[] { 0x80 },
            new byte[] { 0xC0 },
            new byte[] { 0xFF },
        });
        var device = new InlineFakeHidDevice(MakeIdentity(), "test://path/1", fakeStream);

        await using var probe = new HidAnalogProbe(device, descriptor, store, ReportLength);

        probe.Device.Should().Be(MakeIdentity());
        probe.Adapter.Should().BeSameAs(descriptor);
        probe.Status.Should().Be(BackendStatus.Stopped);
        probe.IsHealthy.Should().BeFalse();

        await probe.StartAsync(CancellationToken.None);
        await WaitForAsync(() => store.Get(key).Source == KeyProvenance.Analog, TimeSpan.FromSeconds(2));

        var state = store.Get(key);
        state.Source.Should().Be(KeyProvenance.Analog);
        state.Value.Should().BeGreaterThan(0f);

        await probe.StopAsync(CancellationToken.None);
        fakeStream.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Open_failure_surfaces_FaultedAnalog_without_throwing()
    {
        const ushort scanCode = 0x12;
        var key = KeyId.FromScanCode(scanCode);
        var store = new KeyStateStore(new KeyIndex(new[] { key }));
        var descriptor = MakeDescriptorForKey(scanCode);
        var device = new FailingFakeHidDevice(MakeIdentity(), "test://path/2");

        await using var probe = new HidAnalogProbe(device, descriptor, store, ReportLength);

        BackendStatusChanged? faulted = null;
        probe.StatusChanged += (_, e) =>
        {
            if (e.Status == BackendStatus.FaultedAnalog)
            {
                faulted = e;
            }
        };

        // Must not throw: open failure transitions probe to FaultedAnalog instead.
        await probe.StartAsync(CancellationToken.None);

        probe.Status.Should().Be(BackendStatus.FaultedAnalog);
        probe.IsHealthy.Should().BeFalse();
        faulted.Should().NotBeNull();
        faulted!.Kind.Should().Be(BackendKind.HidAnalog);
        faulted.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SubscribeRaw_returns_disposable_token_and_does_not_break_loop()
    {
        const ushort scanCode = 0x13;
        var key = KeyId.FromScanCode(scanCode);
        var store = new KeyStateStore(new KeyIndex(new[] { key }));
        var descriptor = MakeDescriptorForKey(scanCode);
        var fakeStream = new InlineFakeHidStream(new[] { new byte[] { 0x40 } });
        var device = new InlineFakeHidDevice(MakeIdentity(), "test://path/3", fakeStream);

        await using var probe = new HidAnalogProbe(device, descriptor, store, ReportLength);

        var sub = probe.SubscribeRaw(key, _ => { });
        sub.Should().NotBeNull();

        await probe.StartAsync(CancellationToken.None);
        await WaitForAsync(() => store.Get(key).Source == KeyProvenance.Analog, TimeSpan.FromSeconds(2));

        sub.Dispose();
        // Double-dispose is a no-op
        sub.Dispose();

        await probe.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Status_transitions_through_Starting_Running_Stopped()
    {
        const ushort scanCode = 0x14;
        var key = KeyId.FromScanCode(scanCode);
        var store = new KeyStateStore(new KeyIndex(new[] { key }));
        var descriptor = MakeDescriptorForKey(scanCode);
        var fakeStream = new InlineFakeHidStream(new[] { new byte[] { 0x50 } });
        var device = new InlineFakeHidDevice(MakeIdentity(), "test://path/4", fakeStream);

        await using var probe = new HidAnalogProbe(device, descriptor, store, ReportLength);

        var observed = new List<BackendStatus>();
        var gate = new object();
        probe.StatusChanged += (_, e) =>
        {
            lock (gate) { observed.Add(e.Status); }
        };

        await probe.StartAsync(CancellationToken.None);
        await WaitForAsync(() =>
        {
            lock (gate) { return observed.Contains(BackendStatus.Running); }
        }, TimeSpan.FromSeconds(2));

        await probe.StopAsync(CancellationToken.None);

        lock (gate)
        {
            observed.Should().Contain(BackendStatus.Starting);
            observed.Should().Contain(BackendStatus.Running);
        }
    }

    /// <summary>
    /// Minimal in-test IHidStream — keeps the Win-only test project self-contained
    /// instead of cross-referencing the abstractions test fakes.
    /// </summary>
    private sealed class InlineFakeHidStream : IHidStream
    {
        private readonly Queue<byte[]> _reports;

        public InlineFakeHidStream(IEnumerable<byte[]> reports)
        {
            _reports = new Queue<byte[]>(reports);
        }

        public bool IsDisposed { get; private set; }

        public int Read(Span<byte> buffer)
        {
            if (_reports.Count == 0)
            {
                // Looping behaviour: once we've seen the scripted reports, keep
                // returning the last report so the poll loop has work to do without
                // tripping FaultedAnalog from end-of-script.
                return 0;
            }
            var next = _reports.Dequeue();
            var n = Math.Min(next.Length, buffer.Length);
            next.AsSpan(0, n).CopyTo(buffer);
            // Re-enqueue so subsequent reads keep getting valid frames.
            _reports.Enqueue(next);
            return n;
        }

        public void GetFeature(Span<byte> buffer) { }
        public void SetFeature(ReadOnlySpan<byte> buffer) { }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class InlineFakeHidDevice : IHidDevice
    {
        private readonly IHidStream _stream;

        public InlineFakeHidDevice(DeviceIdentity identity, string devicePath, IHidStream stream)
        {
            Identity = identity;
            DevicePath = devicePath;
            _stream = stream;
        }

        public DeviceIdentity Identity { get; }
        public string DevicePath { get; }

        public IHidStream Open() => _stream;
    }

    private sealed class FailingFakeHidDevice : IHidDevice
    {
        public FailingFakeHidDevice(DeviceIdentity identity, string devicePath)
        {
            Identity = identity;
            DevicePath = devicePath;
        }

        public DeviceIdentity Identity { get; }
        public string DevicePath { get; }

        public IHidStream Open() => throw new IOException("simulated open failure");
    }
}
