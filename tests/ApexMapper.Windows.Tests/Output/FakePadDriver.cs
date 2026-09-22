using ApexMapper.Core.Engine;
using ApexMapper.Windows.Output;

namespace ApexMapper.Windows.Tests.Output;

/// <summary>
/// A pad driver that records what it was asked to do and reads back what was last
/// submitted, as XInput would. Thread-safe. <see cref="OnSubmit"/> can block or throw
/// before a submit lands, to stand in for a wedged or failing driver.
/// </summary>
internal sealed class FakePadDriver : IPadDriver
{
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];
    private readonly HashSet<string?> _readBackThreads = [];
    private PadReport _state;
    private int _submits;
    private int _userIndexQueries;

    /// <summary>User index queries that answer -1 before the slot is reported.</summary>
    public int UserIndexUnreportedFor { get; set; }

    /// <summary>What readback returns while set, instead of the last submitted report.</summary>
    public PadReport? ReadBackOverride { get; set; }

    /// <summary>Nothing is connected in the slot: readback fails, as after an unplug by someone else.</summary>
    public bool Gone { get; set; }

    /// <summary>Record every submitted report in <see cref="Log"/>. Off for allocation tests.</summary>
    public bool LogSubmits { get; set; } = true;

    public Action<PadReport>? OnSubmit { get; set; }

    public Exception? ConnectThrows { get; set; }

    public bool Connected { get; private set; }

    public bool Disposed { get; private set; }

    public int Submits => Volatile.Read(ref _submits);

    public PadReport State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>"connect", "submit LX=.. RT=..", "disconnect", "dispose", in order.</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    public int UserIndex => Interlocked.Increment(ref _userIndexQueries) <= UserIndexUnreportedFor ? -1 : 0;

    public void Connect()
    {
        if (ConnectThrows is { } e)
        {
            throw e;
        }
        lock (_lock)
        {
            Connected = true;
            _log.Add("connect");
        }
    }

    public void Submit(in PadReport report)
    {
        OnSubmit?.Invoke(report);
        lock (_lock)
        {
            _state = report;
            if (LogSubmits)
            {
                _log.Add(Describe(report));
            }
        }
        Interlocked.Increment(ref _submits);
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            Connected = false;
            _log.Add("disconnect");
        }
    }

    /// <summary>Names of the threads that have read the pad back, to prove where driver calls happen.</summary>
    public IReadOnlyCollection<string?> ReadBackThreads
    {
        get
        {
            lock (_lock)
            {
                return [.. _readBackThreads];
            }
        }
    }

    public bool TryReadBack(int userIndex, out PadReport report, out uint packetNumber)
    {
        lock (_lock)
        {
            _readBackThreads.Add(Thread.CurrentThread.Name);
            report = ReadBackOverride ?? _state;
            packetNumber = (uint)_submits;
            return Connected && !Gone && userIndex >= 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Disposed = true;
            _log.Add("dispose");
        }
    }

    public static string Describe(PadReport r) =>
        r == PadReport.Neutral ? "submit neutral" : $"submit LX={r.LeftStickX} RT={r.RightTrigger} LT={r.LeftTrigger} B={r.Buttons}";
}
