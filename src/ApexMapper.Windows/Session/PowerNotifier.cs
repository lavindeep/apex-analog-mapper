using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Session;

/// <summary>Suspend and resume, as the session needs them.</summary>
public interface IPowerEvents
{
    /// <summary>The PC is going to sleep or has woken. Raised on a system thread; handlers must not block.</summary>
    event Action? SleepOrWake;
}

/// <summary>
/// Suspend and resume through <c>PowerRegisterSuspendResumeNotification</c>, which calls
/// back on a system thread and needs no window. One per process, owned by the app for
/// its lifetime. A handler that throws is counted in <see cref="HandlerFaults"/>, never
/// propagated into the system's callback.
/// </summary>
public sealed unsafe class PowerNotifier : IPowerEvents, IDisposable
{
    private static PowerNotifier? s_current;

    private PowrProf.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS* _parameters;
    private nint _registration;
    private int _handlerFaults;

    public PowerNotifier()
    {
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
        {
            throw new InvalidOperationException("Only one power notifier can exist per process.");
        }
        // The system keeps the pointer for the life of the registration, so it lives in native memory.
        _parameters = (PowrProf.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS*)NativeMemory.AllocZeroed((nuint)sizeof(PowrProf.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS));
        _parameters->Callback = &OnPowerEvent;
        var error = PowrProf.PowerRegisterSuspendResumeNotification(PowrProf.DEVICE_NOTIFY_CALLBACK, _parameters, out _registration);
        if (error != 0)
        {
            NativeMemory.Free(_parameters);
            _parameters = null;
            Interlocked.CompareExchange(ref s_current, null, this);
            throw new Win32Exception((int)error, "PowerRegisterSuspendResumeNotification failed.");
        }
    }

    public event Action? SleepOrWake;

    public int HandlerFaults => Volatile.Read(ref _handlerFaults);

    /// <summary>Raises <see cref="SleepOrWake"/> as the system callback would. Internal for tests.</summary>
    internal void Raise()
    {
        try
        {
            SleepOrWake?.Invoke();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }

    public void Dispose()
    {
        if (_registration == 0)
        {
            return;
        }
        PowrProf.PowerUnregisterSuspendResumeNotification(_registration);
        _registration = 0;
        NativeMemory.Free(_parameters);
        _parameters = null;
        Interlocked.CompareExchange(ref s_current, null, this);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint OnPowerEvent(nint context, uint type, nint setting)
    {
        if (type is PowrProf.PBT_APMSUSPEND or PowrProf.PBT_APMRESUMESUSPEND or PowrProf.PBT_APMRESUMEAUTOMATIC)
        {
            s_current?.Raise();
        }
        return 0;
    }
}
