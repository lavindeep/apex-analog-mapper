using System.Runtime.InteropServices;

namespace ApexMapper.Windows.Native;

/// <summary>powrprof: suspend and resume notifications delivered to a callback, so no window is needed.</summary>
internal static unsafe partial class PowrProf
{
    public const uint DEVICE_NOTIFY_CALLBACK = 2;
    public const uint PBT_APMSUSPEND = 0x0004;
    public const uint PBT_APMRESUMESUSPEND = 0x0007;
    public const uint PBT_APMRESUMEAUTOMATIC = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public delegate* unmanaged[Stdcall]<nint, uint, nint, uint> Callback;
        public nint Context;
    }

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerRegisterSuspendResumeNotification(uint flags, DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS* recipient, out nint registrationHandle);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerUnregisterSuspendResumeNotification(nint registrationHandle);
}
