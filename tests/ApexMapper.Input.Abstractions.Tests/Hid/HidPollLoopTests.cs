using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Calibration;
using ApexMapper.Input.Abstractions.Hid;
using ApexMapper.Input.Abstractions.Tests.Fakes;

namespace ApexMapper.Input.Abstractions.Tests.Hid;

public class HidPollLoopTests
{
    private const int ReportLength = 1;

    private static KeyId K(ushort s) => KeyId.FromScanCode(s);

    private static (KeyStateStore store, HidReportParser parser, KeyId key) MakeOneFieldParser()
    {
        var key = K(0x11);
        var store = new KeyStateStore(new KeyIndex(new[] { key }));
        var curve = new CalibrationCurve(0f, 255f, 2f, NormalizationKind.Linear);
        var parser = new HidReportParser(new[]
        {
            new HidReportField(key, ByteOffset: 0, BitWidth: 8, Curve: curve),
        });
        return (store, parser, key);
    }

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
    public async Task Starts_and_runs_reports_through_parser_into_store()
    {
        var (store, parser, key) = MakeOneFieldParser();
        var stream = new FakeHidStream(new[]
        {
            new byte[] { 0x40 },
            new byte[] { 0x80 },
            new byte[] { 0xC0 },
        });
        await using var loop = new HidPollLoop(stream, parser, store, ReportLength);

        using var cts = new CancellationTokenSource();
        await loop.StartAsync(cts.Token);

        await WaitForAsync(() => loop.ReadCount >= 3, TimeSpan.FromSeconds(2));

        await loop.StopAsync(CancellationToken.None);

        loop.ReadCount.Should().BeGreaterThanOrEqualTo(3);
        // Last successful report = 0xC0; should be reflected (some failure ticks may follow but value persists).
        store.Get(key).Value.Should().BeApproximately(0xC0 / 255f, 1e-4f);
        store.Get(key).Source.Should().Be(KeyProvenance.Analog);
    }

    [Fact]
    public async Task Status_transitions_through_Starting_and_Running()
    {
        var (store, parser, _) = MakeOneFieldParser();
        var stream = new FakeHidStream(new[] { new byte[] { 0x10 } });
        await using var loop = new HidPollLoop(stream, parser, store, ReportLength);

        var observed = new List<BackendStatus>();
        var gate = new object();
        loop.StatusChanged += (_, e) =>
        {
            lock (gate)
            {
                observed.Add(e.Status);
            }
        };

        loop.Status.Should().Be(BackendStatus.Stopped);

        using var cts = new CancellationTokenSource();
        await loop.StartAsync(cts.Token);

        // Once StartAsync returns, loop has entered read cycle, so Running must have been observed.
        observed.Should().Contain(BackendStatus.Starting);
        observed.Should().Contain(BackendStatus.Running);
        loop.Status.Should().BeOneOf(BackendStatus.Running, BackendStatus.FaultedAnalog, BackendStatus.Stopped);

        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Idle_zero_byte_reads_never_trip_FaultedAnalog()
    {
        var (store, parser, _) = MakeOneFieldParser();
        // Empty queue -> FakeHidStream.Read returns 0 forever, i.e. a device that
        // is healthy but simply has no report ready. This must stay Running.
        var stream = new FakeHidStream(Array.Empty<byte[]>());
        await using var loop = new HidPollLoop(stream, parser, store, ReportLength, consecutiveFailureThreshold: 3);

        var faulted = false;
        loop.StatusChanged += (_, e) =>
        {
            if (e.Status == BackendStatus.FaultedAnalog)
            {
                faulted = true;
            }
        };

        await loop.StartAsync(CancellationToken.None);
        await Task.Delay(150); // let many idle ticks elapse

        loop.Status.Should().Be(BackendStatus.Running);
        faulted.Should().BeFalse();
        loop.FailureCount.Should().Be(0);

        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Threshold_consecutive_faults_trips_FaultedAnalog()
    {
        var (store, parser, _) = MakeOneFieldParser();
        // A stream that throws forever is a genuinely dead stream.
        var stream = new SequencedHidStream(new Func<int>[]
        {
            () => throw new IOException("dead"),
            () => throw new IOException("dead"),
            () => throw new IOException("dead"),
        });
        await using var loop = new HidPollLoop(stream, parser, store, ReportLength, consecutiveFailureThreshold: 3);

        BackendStatusChanged? faulted = null;
        loop.StatusChanged += (_, e) =>
        {
            if (e.Status == BackendStatus.FaultedAnalog)
            {
                faulted = e;
            }
        };

        await loop.StartAsync(CancellationToken.None);
        await WaitForAsync(() => loop.Status == BackendStatus.FaultedAnalog, TimeSpan.FromSeconds(2));

        loop.FailureCount.Should().BeGreaterThanOrEqualTo(3);
        faulted.Should().NotBeNull();
        faulted!.Kind.Should().Be(BackendKind.HidAnalog);
        faulted.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Successful_read_resets_consecutive_fault_count()
    {
        var (store, parser, _) = MakeOneFieldParser();
        var stream = new SequencedHidStream(new Func<int>[]
        {
            () => 1,                                 // ok
            () => throw new IOException("blip"),     // fault
            () => throw new IOException("blip"),     // fault
            () => 1,                                 // ok -> reset streak
            () => throw new IOException("blip"),     // fault
            () => throw new IOException("blip"),     // fault
            () => 1,                                 // ok -> reset streak
        });
        await using var loop = new HidPollLoop(stream, parser, store, ReportLength, consecutiveFailureThreshold: 3);

        BackendStatusChanged? faulted = null;
        loop.StatusChanged += (_, e) =>
        {
            if (e.Status == BackendStatus.FaultedAnalog)
            {
                faulted = e;
            }
        };

        await loop.StartAsync(CancellationToken.None);

        // Wait for all 7 scripted reads to be consumed (script then goes idle).
        await WaitForAsync(() => stream.CallCount >= 7, TimeSpan.FromSeconds(2));

        // No fault streak ever reached the threshold of 3 because each pair of
        // faults was broken by a success; the 3 successes read through.
        faulted.Should().BeNull();
        loop.ReadCount.Should().BeGreaterThanOrEqualTo(3);

        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Exceptions_count_as_failures_and_trip_FaultedAnalog_after_threshold()
    {
        var (store, parser, _) = MakeOneFieldParser();
        var stream = new SequencedHidStream(new Func<int>[]
        {
            () => 1,  // ok
            () => throw new InvalidOperationException("boom"),
            () => throw new InvalidOperationException("boom"),
            () => throw new InvalidOperationException("boom"),
        });
        await using var loop = new HidPollLoop(stream, parser, store, ReportLength, consecutiveFailureThreshold: 3);

        await loop.StartAsync(CancellationToken.None);
        await WaitForAsync(() => loop.Status == BackendStatus.FaultedAnalog, TimeSpan.FromSeconds(2));

        loop.FailureCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task StopAsync_cancels_loop_cleanly()
    {
        var (store, parser, _) = MakeOneFieldParser();
        // Always-succeeding stream so the loop never trips while waiting for stop.
        var stream = new AlwaysSucceedHidStream();
        await using var loop = new HidPollLoop(stream, parser, store, ReportLength);

        await loop.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        loop.Status.Should().Be(BackendStatus.Running);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await loop.StopAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        loop.Status.Should().Be(BackendStatus.Stopped);
    }

    [Fact]
    public async Task Parsed_byte_writes_analog_provenance_and_normalized_value()
    {
        var (store, parser, key) = MakeOneFieldParser();
        var stream = new FakeHidStream(new[] { new byte[] { 0x80 } });
        await using var loop = new HidPollLoop(stream, parser, store, ReportLength);

        await loop.StartAsync(CancellationToken.None);
        await WaitForAsync(() => loop.ReadCount >= 1, TimeSpan.FromSeconds(2));
        await loop.StopAsync(CancellationToken.None);

        var state = store.Get(key);
        state.Source.Should().Be(KeyProvenance.Analog);
        state.Value.Should().BeApproximately(0x80 / 255f, 1e-3f);
    }

    [Fact]
    public async Task DisposeAsync_stops_running_loop()
    {
        var (store, parser, _) = MakeOneFieldParser();
        var stream = new AlwaysSucceedHidStream();
        var loop = new HidPollLoop(stream, parser, store, ReportLength);

        await loop.StartAsync(CancellationToken.None);
        loop.Status.Should().Be(BackendStatus.Running);

        await loop.DisposeAsync();

        loop.Status.Should().Be(BackendStatus.Stopped);
    }

    /// <summary>
    /// IHidStream test double whose Read behavior is a sequence of Func&lt;int&gt; -
    /// each call may return a byte count, throw, or signal EOF (0).
    /// After the script is exhausted, returns 0 forever.
    /// </summary>
    private sealed class SequencedHidStream : IHidStream
    {
        private readonly Func<int>[] _script;
        private int _index;

        public SequencedHidStream(Func<int>[] script)
        {
            _script = script;
        }

        public int CallCount { get; private set; }

        public int Read(Span<byte> buffer)
        {
            CallCount++;
            var idx = Interlocked.Increment(ref _index) - 1;
            if (idx >= _script.Length)
            {
                return 0;
            }
            var n = _script[idx]();
            if (n > 0 && n <= buffer.Length)
            {
                buffer[0] = 0x42;
            }
            return n;
        }

        public void GetFeature(Span<byte> buffer) { }
        public void SetFeature(ReadOnlySpan<byte> buffer) { }
        public void Dispose() { }
    }

    /// <summary>
    /// IHidStream that always returns a valid 1-byte report. Used for shutdown
    /// tests where we want the loop to keep spinning until cancellation.
    /// </summary>
    private sealed class AlwaysSucceedHidStream : IHidStream
    {
        public int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            buffer[0] = 0x10;
            return Math.Min(1, buffer.Length);
        }

        public void GetFeature(Span<byte> buffer) { }
        public void SetFeature(ReadOnlySpan<byte> buffer) { }
        public void Dispose() { }
    }
}
