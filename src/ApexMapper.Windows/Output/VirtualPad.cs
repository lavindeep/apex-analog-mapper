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
/// report equal to the last one and holds a change back until the second engine tick
/// after the previous submit (500 Hz); a later tick offers it again. Any thread can
/// take the pad away: <see cref="Claim"/> is one interlocked exchange, after which every
/// <see cref="TrySubmit"/> returns false without touching the driver, including the
/// return of a submit that was already inside the driver. <see cref="Unplug"/> claims,
/// zeros, then disconnects; the first caller does the work and later ones wait for it.
/// If the engine is inside a driver call when the pad is taken, the zero may race it;
/// the disconnect that follows is what a game acts on.
/// </summary>
public sealed class VirtualPad : IDisposable
{
    public const int ConnectTimeoutMs = 2000;

    /// <summary>
    /// 500 Hz on the engine's 1 ms ticks: every other tick. Half a tick of slack keeps a
    /// tick that arrives a little early from pushing the change to the third tick.
    /// </summary>
    public const double MinSubmitIntervalMs = 1.5;

    /// <summary>How long a second <see cref="Unplug"/> caller waits for the first to finish.</summary>
    public const int UnplugWaitMs = 2000;

    private const int EngineOwns = 0;
    private const int EngineSubmitting = 1;
    private const int Claimed = 2;

    private readonly IPadDriver _driver;
    private readonly long _minIntervalTicks = (long)(MinSubmitIntervalMs * Stopwatch.Frequency / 1000);
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
            cancel.WaitHandle.WaitOne(10);
        }
        throw new PadException("The virtual controller did not read neutral within 2 seconds of connecting.");
    }

    /// <summary>
    /// Engine thread. False once the pad has been claimed: the engine must stop. True
    /// otherwise, whether the report was sent, was unchanged, or waits for the 500 Hz cap.
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
    /// Claims, submits neutral, then disconnects. Driver errors are kept in
    /// <see cref="UnplugError"/>, never thrown. Idempotent: a later caller waits up to
    /// <see cref="UnplugWaitMs"/> for the first and returns whether it finished.
    /// </summary>
    public bool Unplug()
    {
        Claim();
        if (Interlocked.Exchange(ref _unplugStarted, 1) != 0)
        {
            return _unplugged.Wait(UnplugWaitMs);
        }
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
        return true;
    }

    /// <summary>True while a game can see a controller in this pad's slot. Any thread; costs one XInput read.</summary>
    public bool IsPresent() => _driver.TryReadBack(UserIndex, out _, out _);

    /// <summary>The state a game reads right now, for tests and the loopback measurement.</summary>
    public bool TryReadBack(out PadReport report, out uint packetNumber) => _driver.TryReadBack(UserIndex, out report, out packetNumber);

    /// <summary>Unplugs if nobody has, then releases the driver handle.</summary>
    public void Dispose()
    {
        if (Unplug())
        {
            _driver.Dispose();
            _unplugged.Dispose();
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
