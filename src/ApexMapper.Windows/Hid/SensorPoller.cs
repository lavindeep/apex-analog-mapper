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
/// Built only through <see cref="For"/>, which validates and decides the canary rate.
/// </summary>
public sealed class PollerConfig
{
    private PollerConfig(int[] groups, GroupSignature?[] signatures, int canaryEveryCycles)
    {
        Groups = groups;
        Signatures = signatures;
        CanaryEveryCycles = canaryEveryCycles;
    }

    /// <summary>Sensor groups 1..5 to read, in order.</summary>
    public int[] Groups { get; }

    /// <summary>Expected signature per group (index group - 1); null skips the check for that group.</summary>
    public GroupSignature?[] Signatures { get; }

    /// <summary>
    /// 50 when every polled group has a signature and no two are the same, so a shifted
    /// reply fails on the first exchange. 5 when a shift could go unseen by the
    /// signatures (a missing one, or two groups with the same absent mask, as on ISO
    /// and JIS layouts), so the canary catches it within 5 cycles instead of 50.
    /// </summary>
    public int CanaryEveryCycles { get; }

    /// <summary>True when the signatures alone can detect any shift among the polled groups.</summary>
    public bool SignaturesDistinguishGroups => CanaryEveryCycles == SensorPoller.CanaryEveryCycles;

    public static PollerConfig For(IEnumerable<int> groups, IReadOnlyDictionary<int, GroupSignature>? signatures = null)
    {
        var ordered = groups.Distinct().OrderBy(g => g).ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one sensor group is needed.", nameof(groups));
        }
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
                if (group is < 1 or > SensorRequest.GroupCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(signatures), group, "Sensor group must be 1..5.");
                }
                expected[group - 1] = signature;
            }
        }
        var distinguishable = ordered.Length == 1 || ordered.All(g => expected[g - 1].HasValue);
        for (var i = 0; distinguishable && i < ordered.Length; i++)
        {
            for (var j = i + 1; j < ordered.Length; j++)
            {
                if (expected[ordered[i] - 1] == expected[ordered[j] - 1])
                {
                    distinguishable = false;
                }
            }
        }
        return new PollerConfig(ordered, expected, distinguishable ? SensorPoller.CanaryEveryCycles : SensorPoller.CanaryEveryCyclesWhenBlind);
    }
}

/// <summary>
/// The sensor thread. Opens the vendor interface, verifies the firmware, then polls
/// the configured groups back to back with no floor and no drain, publishing one
/// <see cref="SensorSnapshot"/> per cycle, stamped when the last group lands. Desync
/// defence per the design: every reply is checked structurally, for plausibility, and
/// against its group signature, and a 0x90 canary must match the firmware read at
/// open. Any fault retires the handle; the first reopen is immediate, later ones wait
/// a second on a handle <see cref="Stop"/> signals. A read timeout is a fault; three
/// consecutive slow cycles are a fault; a single slow cycle only leaves the snapshot
/// stale. Nothing thrown on this thread escapes it. The cycle path allocates nothing.
/// </summary>
public sealed class SensorPoller : IDisposable
{
    public const int CanaryEveryCycles = 50;
    public const int CanaryEveryCyclesWhenBlind = 5;
    public const int BackoffMs = 1000;

    /// <summary>How long <see cref="Stop"/> waits for the sensor thread before giving up on it.</summary>
    public const int JoinTimeoutMs = 2000;
    public const string WaitingReason = "No vendor interface found for the selected keyboard.";
    private const int SignatureFaultsBeforeCalibrationHint = 3;

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
    private int _consecutiveFaults;
    private int _signatureFaultsBeforeSuccess;
    private long _cycles;
    private long _handleCycles;

    /// <param name="open">Opens the vendor interface, or returns null when the keyboard is absent. Called on the poller thread.</param>
    /// <param name="shared">The snapshot the engine reads.</param>
    /// <param name="config">Groups and signatures to poll; see <see cref="Reconfigure"/>.</param>
    internal SensorPoller(Func<IVendorStream?> open, SensorSnapshot shared, PollerConfig config)
    {
        _open = open;
        _shared = shared;
        _config = config;
    }

    /// <summary>A poller for the keyboard with the given container id.</summary>
    public static SensorPoller ForKeyboard(Guid containerId, SensorSnapshot shared, PollerConfig config) =>
        new(() => HidVendorDevices.Open(containerId), shared, config);

    public PollerState State => (PollerState)Volatile.Read(ref _state);

    /// <summary>Why the handle was last retired, or why the poller is waiting; null until the first.</summary>
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
        if (Stopping)
        {
            throw new InvalidOperationException("The poller was stopped; create a new one.");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "apex-sensor" };
        _thread.Start();
    }

    /// <summary>Swaps the groups and signatures. A cycle in flight is discarded, not published.</summary>
    public void Reconfigure(PollerConfig config) => Volatile.Write(ref _config, config);

    /// <summary>
    /// Signals the thread, aborts a blocked read, and joins, bounded by
    /// <see cref="JoinTimeoutMs"/>. Returns whether the thread has exited.
    /// </summary>
    public bool Stop()
    {
        Volatile.Write(ref _stop, 1);
        _wake.Set();
        lock (_deviceLock)
        {
            _device?.Abort();
        }
        return _thread?.Join(JoinTimeoutMs) ?? true;
    }

    public void Dispose()
    {
        if (Stop())
        {
            _wake.Dispose();
        }
    }

    /// <summary><see cref="Stop"/> was called. Internal for tests.</summary>
    internal bool Stopping => Volatile.Read(ref _stop) != 0;

    private void SetState(PollerState state) => Volatile.Write(ref _state, (int)state);

    private void Run()
    {
        while (!Stopping)
        {
            try
            {
                Session();
            }
            catch (Exception e)
            {
                Fault("Unexpected error on the sensor thread: " + e.Message);
            }
        }
        Retire();
        SetState(PollerState.Stopped);
    }

    /// <summary>One handle: open, verify, poll until a fault or stop.</summary>
    private void Session()
    {
        SetState(PollerState.Starting);
        var device = TryOpen();
        if (device is null)
        {
            // A stop during the open is not an absent keyboard: keep the last fault.
            if (!Stopping)
            {
                Volatile.Write(ref _faultReason, WaitingReason);
                SetState(PollerState.Waiting);
                Backoff();
            }
            return;
        }
        if (VerifyFirmware(device) is { } reason)
        {
            if (!Stopping)
            {
                Fault(reason);
            }
            return;
        }
        SetState(PollerState.Running);
        _stats.Reset();
        _handleCycles = 0;
        while (!Stopping)
        {
            if (Cycle(device) is { } fault)
            {
                if (!Stopping)
                {
                    Fault(fault);
                }
                return;
            }
        }
    }

    /// <summary>
    /// One cycle: every configured group, publish, canary, period. Returns a fault
    /// reason or null. Internal so the allocation test can drive it directly.
    /// </summary>
    internal string? Cycle(VendorInterface device)
    {
        var cycleStart = Stopwatch.GetTimestamp();
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
            if (!SensorProtocol.IsPlausibleGroup(_raw))
            {
                return "A sensor reply was all zero or all one value, which is not sensor data.";
            }
            if (config.Signatures[group - 1] is { } signature && !signature.Matches(_raw))
            {
                return SignatureFaultReason();
            }
            _working.SetGroup(group, _raw, _filtered);
        }
        _working.Stamp(Stopwatch.GetTimestamp());
        if (ReferenceEquals(config, Volatile.Read(ref _config)))
        {
            _shared.Publish(_working);
        }
        Volatile.Write(ref _consecutiveFaults, 0);
        _signatureFaultsBeforeSuccess = 0;
        var cycles = _cycles + 1;
        Volatile.Write(ref _cycles, cycles);
        _handleCycles++;
        if (_handleCycles % config.CanaryEveryCycles == 0)
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
        var periodMs = (float)((Stopwatch.GetTimestamp() - cycleStart) / _ticksPerMs);
        return _stats.Record(periodMs) ? "Three consecutive sensor cycles of 100 ms or more." : null;
    }

    private string SignatureFaultReason()
    {
        if (_handleCycles == 0)
        {
            _signatureFaultsBeforeSuccess++;
        }
        return _signatureFaultsBeforeSuccess >= SignatureFaultsBeforeCalibrationHint
            ? "The recorded sensor signature does not match this keyboard. Re-run calibration for it."
            : "A sensor reply failed its group signature (shifted or foreign reply).";
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

    internal static string Describe(ExchangeStatus status) => status switch
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
        var consecutive = Interlocked.Increment(ref _consecutiveFaults);
        SetState(PollerState.Faulted);
        if (consecutive > 1)
        {
            Backoff();
        }
    }

    private void Retire()
    {
        lock (_deviceLock)
        {
            _device?.Abort();
            _device = null;
        }
    }

    /// <summary>Waits the backoff, or less when Stop signals.</summary>
    private void Backoff() => _wake.Wait(BackoffMs);
}
