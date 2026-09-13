using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Hid;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Tests.Hid;

public sealed class ApexProAnalogInputTests
{
    private static readonly KeyId W = new(0x11);
    private static readonly KeyId A = new(0x1E);
    private static readonly DiscoveredDevice Keyboard = new(
        new DeviceIdentity(0x1038, 0x1614, null, null, null), "keyboard-a", true,
        PhysicalDeviceId: "container-a");

    [Fact]
    public async Task PublishesWholeCyclesAndRequiresMeasuredReleaseBeforeFirstPress()
    {
        using var stream = new ReplyStream(Firmware(), Group(900, (2, 1400)), Group(900, (1, 1900)));
        await using var input = Source(stream, [Calibration(W), Calibration(A)]);
        var store = Store(W, A);
        input.SetKeys([W, A]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 1400);
        input.ReadinessError.Should().BeNull();
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0);
        store.Get(A).Value.Should().Be(0);

        stream.Enqueue(Group(900), Group(900));
        await RawIs(input, W, 900);
        input.ApplyTo(store);
        stream.Enqueue(Group(900, (2, 1400)));
        await WaitUntil(() => stream.Requests.Count >= 7);
        input.TryGetRaw(W, out var unfinished).Should().BeTrue();
        unfinished.Should().Be(900);
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0);

        stream.Enqueue(Group(900, (1, 1900)));
        await RawIs(input, A, 1900);
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0.5f);
        store.Get(A).Value.Should().Be(1f);
        store.Get(W).Source.Should().Be(KeyProvenance.Analog);
        stream.Requests.Select(request => request[1]).Should().OnlyContain(command => command == 0x90 || command == 0xD7);
        stream.Requests.Where(request => request[1] == 0xD7)
            .Select(request => request[2]).Should().OnlyContain(group => group == 2 || group == 3);
    }

    [Fact]
    public async Task ReleaseGuardIsPerKeyAndAcceptsDescendingCalibration()
    {
        using var stream = new ReplyStream(Firmware(), Group(1900), Group(1900));
        await using var input = Source(stream,
            [new KeyCalibration(W, 1900, 900, 5), Calibration(A)]);
        input.SetKeys([W, A]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 1900);
        var store = Store(W, A);
        input.ApplyTo(store);
        stream.Enqueue(Group(1400), Group(1900));
        await RawIs(input, W, 1400);
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0.5f);
        store.Get(A).Value.Should().Be(0);

        stream.Enqueue(Group(1400), Group(900));
        await RawIs(input, A, 900);
        input.ApplyTo(store);
        stream.Enqueue(Group(1400), Group(1400));
        await RawIs(input, A, 1400);
        input.ApplyTo(store);
        store.Get(A).Value.Should().Be(0.5f);
    }

    [Fact]
    public async Task SlowCycleCannotGiveEarlyGroupsANewTimestamp()
    {
        using var stream = new ReplyStream(Firmware(), Group(900));
        var clock = new ManualClock();
        await using var input = Source(stream, [Calibration(W), Calibration(A)], clock);
        input.SetKeys([W, A]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await WaitUntil(() => stream.Requests.Count >= 3);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        stream.Enqueue(Group(900));
        await WaitUntil(() => input.Status == BackendStatus.FaultedAnalog);
        input.TryGetRaw(W, out _).Should().BeFalse();
        input.TryGetRaw(A, out _).Should().BeFalse();
        input.ReadinessError.Should().Contain("100 ms");
    }

    [Fact]
    public async Task UncalibratedKeysKeepRawOwnershipAndExposeSamplesWithoutClearingGates()
    {
        using var stream = new ReplyStream(Firmware(), Group(900, (2, 1500)));
        await using var input = Source(stream, []);
        var store = Store(W);
        store.Set(W, 1, KeyProvenance.Digital);
        input.SetKeys([W, new(0xE048)]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 1500);

        input.OwnsKey(W).Should().BeTrue();
        input.OwnsKey(new(0xE048)).Should().BeFalse();
        input.RequiredKeys.Should().Equal(W);
        input.ReadinessError.Should().Be("Calibrate keys before starting.");
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0);
        store.IsGated(W).Should().BeTrue();
        input.ApplyTo(store);
        store.IsGated(W).Should().BeTrue();
    }

    [Theory]
    [InlineData(float.NaN, 1900, 5)]
    [InlineData(900, float.PositiveInfinity, 5)]
    [InlineData(-1, 1900, 5)]
    [InlineData(900, 4096, 5)]
    [InlineData(900, 999, 5)]
    [InlineData(900, 1900, -1)]
    [InlineData(900, 1900, float.NaN)]
    [InlineData(900, 1900, 1000)]
    public async Task InvalidCalibrationCannotActivate(float rest, float press, float noise)
    {
        using var stream = new ReplyStream(Firmware(), Group(900), Group(1900));
        await using var input = Source(stream, [new KeyCalibration(W, rest, press, noise)]);
        input.SetKeys([W]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 1900);
        var store = Store(W);
        input.ApplyTo(store);
        input.ReadinessError.Should().NotBeNull();
        input.OwnsKey(W).Should().BeTrue();
        store.Get(W).Value.Should().Be(0);
    }

    [Fact]
    public async Task DuplicateCalibrationFailsClosedAndReturnedCollectionsAreCopied()
    {
        using var stream = new ReplyStream(Firmware(), Group(900));
        var calibrations = new List<KeyCalibration> { Calibration(W) };
        var device = new FakeDevice(() => stream);
        await using var input = new ApexProAnalogInput(_ => device, _ => calibrations, new ManualClock());
        var keys = new List<KeyId> { W };
        input.SetKeys(keys);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 900);
        var store = Store(W);
        input.ApplyTo(store);
        keys.Clear();
        calibrations[0] = new KeyCalibration(W, 0, 1, 0);
        stream.Enqueue(Group(1400));
        await RawIs(input, W, 1400);
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0.5f);
        input.RequiredKeys.Should().Equal(W);

        using var duplicateStream = new ReplyStream(Firmware(), Group(900));
        await using var duplicateInput = Source(duplicateStream, [Calibration(W), Calibration(W)]);
        duplicateInput.SetKeys([W]);
        duplicateInput.SelectDevice(Keyboard);
        await duplicateInput.StartAsync(CancellationToken.None);
        await RawIs(duplicateInput, W, 900);
        duplicateInput.ReadinessError.Should().NotBeNull();
    }

    [Fact]
    public async Task StaleSnapshotGatesHeldKeysAndCannotSupplyUiCalibration()
    {
        using var stream = new ReplyStream(Firmware(), Group(900));
        var clock = new ManualClock();
        await using var input = Source(stream, [Calibration(W)], clock);
        input.SetKeys([W]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 900);
        var store = Store(W);
        input.ApplyTo(store);
        stream.Enqueue(Group(1400));
        await RawIs(input, W, 1400);
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0.5f);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        input.TryGetRaw(W, out _).Should().BeFalse();
        input.ReadinessError.Should().Contain("stale");
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0);
        store.IsGated(W).Should().BeTrue();
        input.ApplyTo(store);
        store.IsGated(W).Should().BeTrue();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("timeout")]
    [InlineData("fault")]
    [InlineData("adc")]
    public async Task FailedCycleRetiresStreamAndPreservesHeldKeyGate(string failure)
    {
        using var stream = new ReplyStream(Firmware(), Group(900));
        var device = new FakeDevice(() => stream);
        await using var input = new ApexProAnalogInput(_ => device, _ => [Calibration(W)], new ManualClock());
        input.SetKeys([W]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 900);
        var store = Store(W);
        input.ApplyTo(store);
        stream.Enqueue(Group(1400));
        await RawIs(input, W, 1400);
        input.ApplyTo(store);

        if (failure == "fault") stream.Fail(new IOException("Disconnected."));
        else stream.Enqueue(failure switch
        {
            "short" => Group(900)[..64],
            "timeout" => [],
            _ => Group(4096),
        });
        await WaitUntil(() => input.Status == BackendStatus.FaultedAnalog);
        input.ApplyTo(store);
        input.OwnsKey(W).Should().BeTrue();
        input.TryGetRaw(W, out _).Should().BeFalse();
        input.ReadinessError.Should().NotBeNull();
        store.Get(W).Value.Should().Be(0);
        store.IsGated(W).Should().BeTrue();
        stream.IsDisposed.Should().BeTrue();
        var requests = stream.Requests.Count;
        await Task.Delay(30);
        device.OpenCount.Should().Be(1);
        stream.Requests.Count.Should().Be(requests);
    }

    [Fact]
    public async Task ReconnectionReverifiesFirmwareAndHeldKeysMustReleaseAgain()
    {
        using var first = new ReplyStream(Firmware(), Group(900));
        using var second = new ReplyStream(Firmware(), Group(1900));
        var streams = new Queue<ReplyStream>([first, second]);
        var device = new FakeDevice(() => streams.Dequeue());
        await using var input = new ApexProAnalogInput(_ => device, _ => [Calibration(W)], new ManualClock());
        input.SetKeys([W]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 900);
        var store = Store(W);
        input.ApplyTo(store);
        first.Enqueue(Group(1400));
        await RawIs(input, W, 1400);
        input.ApplyTo(store);
        first.Fail(new IOException("Disconnected."));
        await WaitUntil(() => input.Status == BackendStatus.FaultedAnalog);
        input.ApplyTo(store);
        var retry = Stopwatch.StartNew();
        await RawIs(input, W, 1900);
        retry.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800));
        first.IsDisposed.Should().BeTrue();
        second.Requests.First()[1].Should().Be(0x90);
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0);
        store.IsGated(W).Should().BeTrue();

        second.Enqueue(Group(900));
        await RawIs(input, W, 900);
        input.ApplyTo(store);
        store.IsGated(W).Should().BeFalse();
        second.Enqueue(Group(1400));
        await RawIs(input, W, 1400);
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0.5f);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("keys")]
    [InlineData("calibration")]
    public async Task ConfigurationChangeRejectsCompletionFromPreviousGeneration(string change)
    {
        using var first = new ReplyStream(Firmware(), Group(900));
        using var second = new ReplyStream(Firmware());
        var streams = new Queue<ReplyStream>([first, second]);
        var device = new FakeDevice(() => streams.Dequeue());
        await using var input = new ApexProAnalogInput(_ => device,
            _ => [Calibration(W), Calibration(A)], new ManualClock());
        input.SetKeys([W]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 900);
        var store = Store(W, A);
        input.ApplyTo(store);
        await WaitUntil(() => first.Requests.Count >= 3);

        if (change == "selection") input.SelectDevice(Keyboard with { DevicePath = "keyboard-b" });
        else if (change == "keys") input.SetKeys([A]);
        else input.ReloadCalibration();
        input.TryGetRaw(W, out _).Should().BeFalse();
        first.Enqueue(Group(1900));
        await WaitUntil(() => second.Requests.Count >= 2);
        input.TryGetRaw(W, out _).Should().BeFalse();
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0);
        first.IsDisposed.Should().BeTrue();

        var active = change == "keys" ? A : W;
        second.Enqueue(Group(1900));
        await RawIs(input, active, 1900);
        input.ApplyTo(store);
        store.Get(active).Value.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedFirmwareNeverSendsSensorRequests()
    {
        using var stream = new ReplyStream(Firmware("4.16.8"));
        await using var input = Source(stream, [Calibration(W)]);
        input.SetKeys([W]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await WaitUntil(() => input.Status == BackendStatus.FaultedAnalog);
        stream.Requests.Should().ContainSingle();
        stream.Requests.First()[1].Should().Be(0x90);
        input.ReadinessError.Should().Contain("4.9.1");
    }

    [Fact]
    public async Task StopAndRestartWaitForOldStreamBeforeOpeningAnother()
    {
        using var first = new ReplyStream(Firmware(), Group(900));
        using var second = new ReplyStream(Firmware(), Group(1900));
        var streams = new Queue<ReplyStream>([first, second]);
        var device = new FakeDevice(() => streams.Dequeue());
        await using var input = new ApexProAnalogInput(_ => device, _ => [Calibration(W)], new ManualClock());
        input.SetKeys([W]);
        input.SelectDevice(Keyboard);
        await input.StartAsync(CancellationToken.None);
        await RawIs(input, W, 900);
        await WaitUntil(() => first.Requests.Count >= 3);
        var stopped = input.StopAsync(CancellationToken.None);
        var started = input.StartAsync(CancellationToken.None);
        input.TryGetRaw(W, out _).Should().BeFalse();
        first.Enqueue(Group(1900));
        await stopped;
        await started;
        await RawIs(input, W, 1900);
        first.IsDisposed.Should().BeTrue();
        device.OpenCount.Should().Be(2);
        var store = Store(W);
        input.ApplyTo(store);
        store.Get(W).Value.Should().Be(0);
        await input.DisposeAsync();
        await input.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void MapCoversDistinctSet1SensorsAndExcludesMechanicalOrAmbiguousKeys()
    {
        foreach (var code in new ushort[] { 0x11, 0x1E, 0x1F, 0x20, 0x39, 0x0E, 0xE01D, 0xE038, 0x56, 0x73, 0x7D })
            ApexProSensorMap.Supports(new(code)).Should().BeTrue();
        foreach (var code in new ushort[] { 0, 0x01, 0x3B, 0xE048, 0x2B })
            ApexProSensorMap.Supports(new(code)).Should().BeFalse();
        Enumerable.Range(0, ushort.MaxValue + 1)
            .Count(code => ApexProSensorMap.Supports(new((ushort)code))).Should().Be(65);
        ApexProDeviceResolver.Resolve(Keyboard with { PhysicalDeviceId = null }).Should().BeNull();
        ApexProDeviceResolver.Resolve(Keyboard with { Identity = Keyboard.Identity with { ProductId = 0x161C } })
            .Should().BeNull();
    }

    private static ApexProAnalogInput Source(ReplyStream stream, IReadOnlyList<KeyCalibration> calibration,
        ManualClock? clock = null) => new(_ => new FakeDevice(() => stream), _ => calibration, clock ?? new ManualClock());

    private static KeyCalibration Calibration(KeyId key) => new(key, 900, 1900, 5);
    private static KeyStateStore Store(params KeyId[] keys) => new(new KeyIndex(keys));
    private static Task RawIs(ApexProAnalogInput input, KeyId key, float expected) =>
        WaitUntil(() => input.TryGetRaw(key, out var actual) && actual == expected);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(5);
        condition().Should().BeTrue("the worker should reach the expected state within three seconds");
    }

    private static byte[] Firmware(string version = "4.9.1")
    {
        var bytes = new byte[65];
        System.Text.Encoding.ASCII.GetBytes(version).CopyTo(bytes, 1);
        return bytes;
    }

    private static byte[] Group(ushort value, params (int Slot, ushort Value)[] overrides)
    {
        var bytes = new byte[65];
        for (var slot = 0; slot < 14; slot++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(29 + slot * 2, 2), value);
        foreach (var sample in overrides)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(29 + sample.Slot * 2, 2), sample.Value);
        return bytes;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }

    private sealed class FakeDevice(Func<IHidStream> open) : IHidDevice
    {
        private int _openCount;
        public DeviceIdentity Identity => Keyboard.Identity;
        public string DevicePath => "sensor-interface";
        public int OpenCount => Volatile.Read(ref _openCount);
        public IHidStream Open()
        {
            Interlocked.Increment(ref _openCount);
            return open();
        }
    }

    private sealed class ReplyStream(params byte[][] replies) : IHidStream
    {
        private readonly BlockingCollection<object> _replies = new(new ConcurrentQueue<object>(replies));
        private int _disposed;
        public ConcurrentQueue<byte[]> Requests { get; } = new();
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public void Enqueue(params byte[][] responses)
        {
            foreach (var response in responses) _replies.Add(response);
        }
        public void Fail(Exception exception) => _replies.Add(exception);
        public void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Requests.Enqueue(buffer.ToArray());
        }
        public int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (!_replies.TryTake(out var response, 500)) throw new TimeoutException("No scripted reply.");
            if (response is Exception error) throw error;
            var bytes = (byte[])response;
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        public void GetFeature(Span<byte> buffer) => throw new NotSupportedException();
        public void SetFeature(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
