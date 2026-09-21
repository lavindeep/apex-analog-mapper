using System.Diagnostics;
using ApexMapper.Core.Sensors;

namespace ApexMapper.Windows.Hid;

public enum PollerState
{
    Stopped,
    /// <summary>Opening the device and verifying its firmware.</summary>
    Starting,
    Running,
    /// <summary>The handle was retired for the reason in <see cref="SensorPoller.FaultReason"/>; reopening after the backoff.</summary>
    Faulted,
    /// <summary>No vendor interface for the selected keyboard; retrying after the backoff.</summary>
    Waiting,
}

/// <summary>
/// What the poller reads each cycle. Swapping the instance is the generation change: a
/// reply in flight when the configuration changes is discarded, never published.
/// </summary>
/// <param name="Groups">Sensor groups 1..5 the profile needs, in read order.</param>
/// <param name="Signatures">Expected signature per group (index group - 1), or null to skip the check for that group (calibration card, before a signature exists).</param>
public sealed record PollerConfig(int[] Groups, GroupSignature?[] Signatures)
{
    public static PollerConfig For(IEnumerable<int> groups, IReadOnlyDictionary<int, GroupSignature>? signatures = null)
    {
        var ordered = groups.Distinct().OrderBy(g => g).ToArray();
        foreach (var group in ordered)
        {
            if (group is < 1 or > SensorRequest.GroupCount)
            {
                throw new ArgumentOutOfRangeException(nameof(groups), group, "Sensor group must be 1..5.");
            }
        }
        var expected = new GroupSignature?[SensorRequest.GroupCount];
        if (signatures is not null)
        {
            foreach (var (group, signature) in signatures)
            {
                expected[group - 1] = signature;
            }
        }
        return new PollerConfig(ordered, expected);
    }
}

/// <summary>
/// The sensor thread. Opens the vendor interface, verifies the firmware, then polls
/// the configured groups back to back with no floor and no drain, publishing one
/// <see cref="SensorSnapshot"/> per cycle. Desync defence per the design: every reply is
/// checked structurally and against its group signature, and a 0x90 canary every
/// fifty cycles must match the firmware read at open. Any fault retires the handle,
/// waits one second on a handle <see cref="Stop"/> signals, reopens and re-verifies.
/// A read timeout is a fault; three consecutive slow cycles are a fault; a single slow
/// cycle only leaves the snapshot stale. The cycle path allocates nothing.
/// </summary>
public sealed class SensorPoller : IDisposable
{
    public const int CanaryEveryCycles = 50;
    public const int BackoffMs = 1000;

    private readonly Func<IVendorStream?> _open;
    private readonly SensorSnapshot _shared;
    private readonly SensorSnapshot _working = new();
    private readonly CycleStats _stats = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly byte[] _reply = new byte[SensorProtocol.ReportLength];
    private readonly ushort[] _raw = new ushort[SensorProtocol.SensorsPerGroup];
    private readonly ushort[] _filtered = new ushort[SensorProtocol.SensorsPerGroup];
    private readonly byte[] _firmwareBytes = new byte[SensorProtocol.ReportLength];
    private readonly Lock _deviceLock = new();
    private readonly double _ticksPerMs = Stopwatch.Frequency / 1000d;
    private PollerConfig _config;
    private VendorInterface? _device;
    private Thread? _thread;
    private int _stop;
    private int _firmwareLength;
    private int _state;
    private string? _faultReason;
    private string _firmware = string.Empty;
    private int _faultCount;
    private long _cycles;

    /// <param name="open">Opens the vendor interface, or returns null when the keyboard is absent. Called on the poller thread.</param>
    /// <param name="shared">The snapshot the engine reads.</param>
    /// <param name="config">Groups and signatures to poll; see <see cref="Reconfigure"/>.</param>
    public SensorPoller(Func<IVendorStream?> open, SensorSnapshot shared, PollerConfig config)
    {
        _open = open;
        _shared = shared;
        _config = config;
    }

    /// <summary>A poller for the keyboard with the given container id.</summary>
    public static SensorPoller ForKeyboard(Guid containerId, SensorSnapshot shared, PollerConfig config) =>
        new(() => HidVendorDevices.Open(containerId), shared, config);

    public PollerState State => (PollerState)Volatile.Read(ref _state);

    /// <summary>Why the handle was last retired; null until the first fault.</summary>
    public string? FaultReason => Volatile.Read(ref _faultReason);

    /// <summary>Firmware string read when the device was last opened.</summary>
    public string Firmware => Volatile.Read(ref _firmware);

    public int FaultCount => Volatile.Read(ref _faultCount);

    public long Cycles => Volatile.Read(ref _cycles);

    /// <summary>Cycle periods of the current handle, for the status card. Read from one thread at a time.</summary>
    public CycleStats Stats => _stats;

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The poller was already started.");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "apex-sensor", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Swaps the groups and signatures. A cycle in flight is discarded, not published.</summary>
    public void Reconfigure(PollerConfig config) => Volatile.Write(ref _config, config);

    /// <summary>Signals the thread, aborts a blocked read, and joins.</summary>
    public void Stop()
    {
        Volatile.Write(ref _stop, 1);
        _wake.Set();
        lock (_deviceLock)
        {
            _device?.Abort();
        }
        _thread?.Join();
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    private bool Stopping => Volatile.Read(ref _stop) != 0;

    private void SetState(PollerState state) => Volatile.Write(ref _state, (int)state);

    private void Run()
    {
        while (!Stopping)
        {
            SetState(PollerState.Starting);
            var device = TryOpen();
            if (device is null)
            {
                SetState(PollerState.Waiting);
                if (Backoff())
                {
                    break;
                }
                continue;
            }
            if (VerifyFirmware(device) is { } reason)
            {
                Fault(reason);
                continue;
            }
            SetState(PollerState.Running);
            _stats.Reset();
            _cycles = 0;
            var lastCycleStart = Stopwatch.GetTimestamp();
            while (!Stopping)
            {
                var cycleStart = Stopwatch.GetTimestamp();
                if (Cycle(device, cycleStart) is { } fault)
                {
                    if (!Stopping)
                    {
                        Fault(fault);
                    }
                    break;
                }
                var periodMs = (float)((cycleStart - lastCycleStart) / _ticksPerMs);
                lastCycleStart = cycleStart;
                if (_stats.Record(periodMs))
                {
                    Fault("Three consecutive sensor cycles of 100 ms or more.");
                    break;
                }
            }
        }
        Retire();
        SetState(PollerState.Stopped);
    }

    /// <summary>One cycle: every configured group, publish, canary. Returns a fault reason or null.</summary>
    internal string? Cycle(VendorInterface device, long cycleStart)
    {
        var config = Volatile.Read(ref _config);
        _working.Begin(cycleStart);
        var groups = config.Groups;
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var status = device.Exchange(SensorRequest.Group(group), _reply);
            if (status != ExchangeStatus.Ok)
            {
                return Describe(status);
            }
            if (SensorProtocol.ParseGroup(_reply, _raw, _filtered) is { } error)
            {
                return error;
            }
            if (config.Signatures[group - 1] is { } signature && !signature.Matches(_raw))
            {
                return "A sensor reply failed its group signature (shifted or foreign reply).";
            }
            _working.SetGroup(group, _raw, _filtered);
        }
        if (ReferenceEquals(config, Volatile.Read(ref _config)))
        {
            _shared.Publish(_working);
        }
        var cycles = _cycles + 1;
        Volatile.Write(ref _cycles, cycles);
        if (cycles % CanaryEveryCycles == 0)
        {
            var status = device.Exchange(SensorRequest.Firmware(), _reply);
            if (status != ExchangeStatus.Ok)
            {
                return Describe(status);
            }
            if (!CanaryMatches())
            {
                return "The firmware canary did not match the firmware read at open (desynchronised replies).";
            }
        }
        return null;
    }

    private bool CanaryMatches()
    {
        var expected = _firmwareBytes.AsSpan(0, _firmwareLength);
        var actual = _reply.AsSpan(1, _firmwareLength);
        if (!actual.SequenceEqual(expected))
        {
            return false;
        }
        for (var i = 1 + _firmwareLength; i < _reply.Length; i++)
        {
            if (_reply[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static string Describe(ExchangeStatus status) => status switch
    {
        ExchangeStatus.Timeout => "The keyboard did not answer within 150 ms.",
        ExchangeStatus.ShortReply => "The keyboard sent a short reply.",
        ExchangeStatus.BadReportId => "The keyboard sent a reply with a non-zero report id.",
        ExchangeStatus.Closed => "The keyboard handle was closed.",
        _ => "The keyboard exchange failed.",
    };

    private VendorInterface? TryOpen()
    {
        IVendorStream? stream;
        try
        {
            stream = _open();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            stream = null;
        }
        if (stream is null)
        {
            return null;
        }
        var device = new VendorInterface(stream);
        lock (_deviceLock)
        {
            if (Stopping)
            {
                device.Abort();
                return null;
            }
            _device = device;
        }
        return device;
    }

    internal string? VerifyFirmware(VendorInterface device)
    {
        var status = device.Exchange(SensorRequest.Firmware(), _reply);
        if (status != ExchangeStatus.Ok)
        {
            return Describe(status);
        }
        if (SensorProtocol.ParseFirmware(_reply, out var version) is { } error)
        {
            return error;
        }
        var text = _reply.AsSpan(1);
        _firmwareLength = text.IndexOf((byte)0) is var end and >= 0 ? end : text.Length;
        text[.._firmwareLength].CopyTo(_firmwareBytes);
        Volatile.Write(ref _firmware, version);
        return null;
    }

    private void Fault(string reason)
    {
        Retire();
        Volatile.Write(ref _faultReason, reason);
        Interlocked.Increment(ref _faultCount);
        SetState(PollerState.Faulted);
        Backoff();
    }

    private void Retire()
    {
        lock (_deviceLock)
        {
            _device?.Abort();
            _device = null;
        }
    }

    /// <summary>Waits the backoff, or less when Stop signals. True when stopping.</summary>
    private bool Backoff()
    {
        _wake.Wait(BackoffMs);
        return Stopping;
    }
}
