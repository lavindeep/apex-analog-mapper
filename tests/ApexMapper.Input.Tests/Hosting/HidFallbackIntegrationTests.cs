using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Adapters;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Abstractions.Hosting;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Hid;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Tests.Hosting;

[Trait("os", "windows")]
public class HidFallbackIntegrationTests
{
    private const int ReportLength = 1;

    private static DeviceAdapterDescriptor MakeDescriptor() =>
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
                    ScanCode: 0x1E,
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

    [Fact]
    public async Task Real_probe_open_failure_surfaces_FaultedAnalog_via_InputHost_without_throwing()
    {
        var key = KeyId.FromScanCode(0x1E);
        var store = new KeyStateStore(new KeyIndex(new[] { key }));
        var descriptor = MakeDescriptor();
        var device = new FailingHidDevice(
            new DeviceIdentity(0x1234, 0x5678, "SN-FAIL", "Fake", "Fake"),
            "test://path/fail");

        var probe = new HidAnalogProbe(device, descriptor, store, ReportLength);

        var ring = new SpscRingBuffer<RawKeyEvent>(64);
        var raw = new InertRawInputAdapter();
        var enumerator = new EmptyDeviceEnumerator();
        DeviceRegistry registry = new(null, Array.Empty<KeyCalibration>());
        var selector = new DeviceSelector(enumerator, () => registry, r => registry = r);
        selector.Initialize();

        await using var host = new InputHost(raw, probe, selector, ring, store);

        Func<Task> act = () => host.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        host.DigitalStatus.Should().Be(BackendStatus.Running);
        host.AnalogStatus.Should().Be(BackendStatus.FaultedAnalog);
    }

    private sealed class FailingHidDevice : IHidDevice
    {
        public FailingHidDevice(DeviceIdentity identity, string path)
        {
            Identity = identity;
            DevicePath = path;
        }

        public DeviceIdentity Identity { get; }
        public string DevicePath { get; }

        public IHidStream Open() => throw new IOException("analog probe blocked by gg");
    }

    private sealed class InertRawInputAdapter : IRawInputAdapter
    {
        public BackendStatus Status { get; private set; } = BackendStatus.Stopped;

        public event EventHandler<BackendStatusChanged>? StatusChanged;
        public event EventHandler<RawInputDeviceChanged>? DeviceChanged;

        public Task StartAsync(CancellationToken ct)
        {
            Status = BackendStatus.Running;
            StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.RawInput, Status, null));
            _ = DeviceChanged;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Status = BackendStatus.Stopped;
            StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.RawInput, Status, null));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyDeviceEnumerator : IDeviceEnumerator
    {
        public IReadOnlyList<DiscoveredDevice> Enumerate() => Array.Empty<DiscoveredDevice>();
    }
}
