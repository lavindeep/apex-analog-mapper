using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Output;

public enum DriverState
{
    Running,

    /// <summary>Installed but not running, which is what a pending reboot after install looks like.</summary>
    NotStarted,

    Missing,

    /// <summary>The service manager could not be asked; connecting the pad will tell.</summary>
    Unknown,
}

/// <summary>
/// Is the ViGEmBus driver service installed and running. Asks the service manager
/// only; the app never downloads or runs the driver installer.
/// </summary>
public static class DriverStatus
{
    public const string ServiceName = "ViGEmBus";

    public static DriverState Query()
    {
        var manager = Advapi32.OpenSCManagerW(null, null, Advapi32.SC_MANAGER_CONNECT);
        if (manager == 0)
        {
            return DriverState.Unknown;
        }
        try
        {
            var service = Advapi32.OpenServiceW(manager, ServiceName, Advapi32.SERVICE_QUERY_STATUS);
            if (service == 0)
            {
                return Marshal.GetLastWin32Error() == Advapi32.ERROR_SERVICE_DOES_NOT_EXIST ? DriverState.Missing : DriverState.Unknown;
            }
            try
            {
                if (!Advapi32.QueryServiceStatus(service, out var status))
                {
                    return DriverState.Unknown;
                }
                return status.dwCurrentState == Advapi32.SERVICE_RUNNING ? DriverState.Running : DriverState.NotStarted;
            }
            finally
            {
                Advapi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }
}
