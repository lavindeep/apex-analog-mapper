using System.Diagnostics;
using ApexMapper.Core.Engine;
using Nefarius.ViGEm.Client.Exceptions;

namespace ApexMapper.Windows.Output;

/// <summary>A driver failure, with a message written for the status card.</summary>
public sealed class PadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The virtual pad for one session.
///
/// <see cref="Connect(CancellationToken)"/> plugs the pad in and returns only once a
/// game reading its XInput slot would see neutral: it submits a one-count stick nudge
/// and a zero, then reads back until every channel is zero, for up to two seconds.
///
/// The engine owns the pad and submits through <see cref="TrySubmit"/>, which skips a
/// report equal to the last one and holds a change back until
/// <see cref="MinSubmitIntervalMs"/> after the previous submit; a later tick offers it
/// again. Any thread can take the pad away: <see cref="Claim"/> is one interlocked
/// exchange, after which every <see cref="TrySubmit"/> returns false without touching
/// the driver, including the return of a submit that was already inside the driver.
///
/// Unplugging zeros, then disconnects, on a worker thread of its own, so a driver that
/// hangs holds nobody but that worker. <see cref="BeginUnplug"/> claims and starts it
/// without waiting (the watchdog, on the hook thread); <see cref="Unplug"/> also waits,
/// at most <see cref="UnplugWaitMs"/>. Whoever comes first starts the worker, and every
/// caller waits on the same result. If the engine is inside a driver call when the pad
/// is taken, the zero may race it; the disconnect that follows is what a game acts on.
/// </summary>
public sealed class VirtualPad : IDisposable
{
    public const int ConnectTimeoutMs = 2000;

    /// <summary>
    /// The least time between two submits. The engine's 1 ms waitable timer measured a
    /// p50 period of 1.51 ms, so this admits about one change per tick and
    /// averages under 500 a second (486 to 490 measured). A 2 ms floor made every change
    /// wait for the second tick and measured 438.
    /// </summary>
    public const double MinSubmitIntervalMs = 1.5;

    /// <summary>The bound on <see cref="Unplug"/>: the plan's 2 s for the pad step, far above the sub-millisecond measured zero and disconnect.</summary>
    public const int UnplugWaitMs = 2000;

    private const int EngineOwns = 0;
    private const int EngineSubmitting = 1;
    private const int Claimed = 2;

    private const int UnplugNotStarted = 0;
    private const int UnplugRunning = 1;
    private const int UnplugThreadFailed = 2;

    private readonly IPadDriver _driver;
    private readonly long _minIntervalTicks = (long)(MinSubmitIntervalMs * Stopwatch.Frequency / 1000);

    // Never disposed: a caller may still arrive to wait on it after the pad was disposed,
    // and without WaitHandle being touched it holds no kernel handle.
    private readonly ManualResetEventSlim _unplugged = new(false);
    private int _owner;
    private int _unplugStarted;
    private PadReport _last;
    private long _lastSubmitTicks = long.MinValue / 2;
    private long _submitCount;
    private string? _unplugError;
    private VirtualPad(IPadDriver driver, int userIndex)
    {
        _driver = driver;
        UserIndex = userIndex;
    }

    /// <summary>The XInput slot a game sees this pad in.</summary>
    public int UserIndex { get; }

    /// <summary>Reports the driver accepted, for the status card's submit rate.</summary>
    public long SubmitCount => Volatile.Read(ref _submitCount);

    /// <summary>Stopwatch timestamp the engine passed with the last report sent to the driver, for the loopback measurement.</summary>
    public long LastSubmitTicks => Volatile.Read(ref _lastSubmitTicks);

    /// <summary>The engine no longer owns the pad.</summary>
    public bool IsClaimed => Volatile.Read(ref _owner) == Claimed;

    /// <summary>Zero and disconnect have both been attempted.</summary>
    public bool IsUnplugged => _unplugged.IsSet;

    /// <summary>Why the zero or the disconnect failed, if either did.</summary>
    public string? UnplugError => Volatile.Read(ref _unplugError);

    /// <summary>Plugs a ViGEm pad in and waits for it to read neutral. Throws <see cref="PadException"/>, or <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static VirtualPad Connect(CancellationToken cancel) => Connect(() => new ViGEmDriver(), cancel);

    internal static VirtualPad Connect(Func<IPadDriver> open, CancellationToken cancel, int timeoutMs = ConnectTimeoutMs)
    {
        IPadDriver driver;
        try
        {
            driver = open();
        }
        catch (Exception e)
        {
            throw new PadException(Describe(e), e);
        }
        var connected = false;
        try
        {
            driver.Connect();
            connected = true;
            var userIndex = WaitForNeutral(driver, cancel, timeoutMs);
            return new VirtualPad(driver, userIndex);
        }
        catch (Exception e)
        {
            if (connected)
            {
                try
                {
                    driver.Disconnect();
                }
                catch (Exception)
                {
                    // The connect already failed; the dispose below releases the handle, which unplugs it.
                }
            }
            driver.Dispose();
            if (e is OperationCanceledException or PadException)
            {
                throw;
            }
            throw new PadException(Describe(e), e);
        }
    }

    /// <summary>
    /// Nudge and zero, then read back until neutral. A pad that was just connected can
    /// report its slot late, and can hold a stale state until a report changes it.
    /// </summary>
    private static int WaitForNeutral(IPadDriver driver, CancellationToken cancel, int timeoutMs)
    {
        var nudge = PadReport.Neutral with { LeftStickX = 1 };
        var clock = Stopwatch.StartNew();
        var nudged = false;
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            cancel.ThrowIfCancellationRequested();
            var userIndex = driver.UserIndex;
            if (userIndex >= 0)
            {
                if (nudged && driver.TryReadBack(userIndex, out var state, out _) && state == PadReport.Neutral)
                {
                    return userIndex;
                }
                driver.Submit(nudge);
                driver.Submit(PadReport.Neutral);
                nudged = true;
            }
            // A sleep, not the token's wait handle, which would create a kernel event per connect.
            Thread.Sleep(10);
        }
        throw new PadException("The virtual controller did not read neutral within 2 seconds of connecting.");
    }

    /// <summary>
    /// Engine thread. False once the pad has been claimed: the engine must stop. True
    /// otherwise, whether the report was sent, was unchanged, or waits for the interval.
    /// Throws <see cref="PadException"/> when the driver fails.
    /// </summary>
    public bool TrySubmit(in PadReport report, long nowTicks)
    {
        if (Interlocked.CompareExchange(ref _owner, EngineSubmitting, EngineOwns) != EngineOwns)
        {
            return false;
        }
        try
        {
            if (report == _last || nowTicks - _lastSubmitTicks < _minIntervalTicks)
            {
                return true;
            }
            // Published before the call, so a reader that sees the report also sees its tick.
            Volatile.Write(ref _lastSubmitTicks, nowTicks);
            _driver.Submit(report);
            _last = report;
            Interlocked.Increment(ref _submitCount);
            return true;
        }
        catch (Exception e)
        {
            throw new PadException(Describe(e), e);
        }
        finally
        {
            // Fails when the pad was claimed during the call; the claim stands.
            Interlocked.CompareExchange(ref _owner, EngineOwns, EngineSubmitting);
        }
    }

    /// <summary>Takes the pad from the engine. True for the first claimer. Never touches the driver.</summary>
    public bool Claim() => Interlocked.Exchange(ref _owner, Claimed) != Claimed;

    /// <summary>
    /// Claims and starts the zero and disconnect on a worker, returning at once. Safe on
    /// the hook thread. If no thread can be started, whichever <see cref="Unplug"/> is
    /// waiting, or comes next, does the work on its own thread instead.
    /// </summary>
    public void BeginUnplug()
    {
        Claim();
        if (Interlocked.CompareExchange(ref _unplugStarted, UnplugRunning, UnplugNotStarted) != UnplugNotStarted)
        {
            return;
        }
        try
        {
            new Thread(UnplugNow) { IsBackground = true, Name = "apex-unplug" }.Start();
        }
        catch (Exception)
        {
            Volatile.Write(ref _unplugStarted, UnplugThreadFailed);
        }
    }

    /// <summary>
    /// Claims, zeros, disconnects, and waits at most <see cref="UnplugWaitMs"/>. Returns
    /// whether both were attempted in time. Driver errors are kept in
    /// <see cref="UnplugError"/>, never thrown. Any number of callers, from any thread.
    /// </summary>
    public bool Unplug()
    {
        BeginUnplug();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (Interlocked.CompareExchange(ref _unplugStarted, UnplugRunning, UnplugThreadFailed) == UnplugThreadFailed)
            {
                UnplugNow();
            }
            var left = UnplugWaitMs - (int)clock.ElapsedMilliseconds;
            if (left <= 0)
            {
                return _unplugged.IsSet;
            }
            if (_unplugged.Wait(Math.Min(left, 50)))
            {
                return true;
            }
        }
    }

    private void UnplugNow()
    {
        try
        {
            _driver.Submit(PadReport.Neutral);
        }
        catch (Exception e)
        {
            Volatile.Write(ref _unplugError, Describe(e));
        }
        try
        {
            _driver.Disconnect();
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _unplugError, Describe(e), null);
        }
        _unplugged.Set();
    }

    /// <summary>True while a game can see a controller in this pad's slot. Any thread; costs one XInput read.</summary>
    public bool IsPresent() => _driver.TryReadBack(UserIndex, out _, out _);

    /// <summary>The state a game reads right now, for tests and the loopback measurement.</summary>
    public bool TryReadBack(out PadReport report, out uint packetNumber) => _driver.TryReadBack(UserIndex, out report, out packetNumber);

    /// <summary>Unplugs if nobody has, then releases the driver handle, unless the unplug is still stuck in the driver.</summary>
    public void Dispose()
    {
        if (Unplug())
        {
            _driver.Dispose();
        }
    }

    /// <summary>A driver exception as a sentence for the status card.</summary>
    internal static string Describe(Exception e) => e switch
    {
        PadException => e.Message,
        VigemBusNotFoundException => "The ViGEmBus driver is not installed. Install it from https://github.com/nefarius/ViGEmBus/releases, then try again.",
        VigemBusVersionMismatchException => "The installed ViGEmBus driver is a version this app cannot use. Install the latest release from https://github.com/nefarius/ViGEmBus/releases.",
        VigemBusAccessFailedException => "Windows denied access to the ViGEmBus driver.",
        VigemNoFreeSlotException => "Every controller slot is in use. Disconnect a controller, then try again.",
        VigemTargetNotPluggedInException => "The virtual controller was unplugged by the driver.",
        DllNotFoundException or BadImageFormatException => "The ViGEm client library could not be loaded.",
        _ => "The virtual controller failed: " + e.Message,
    };
}
