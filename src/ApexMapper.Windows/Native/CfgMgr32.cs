using System.Runtime.InteropServices;

namespace ApexMapper.Windows.Native;

/// <summary>
/// cfgmgr32: the container id of a device interface, which is the same for every
/// interface of one physical keyboard and survives a replug (session 2).
/// </summary>
internal static unsafe partial class CfgMgr32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    public static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    public static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new() { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_Interface_PropertyW(string pszDeviceInterface, in DEVPROPKEY propertyKey, out uint propertyType, byte* propertyBuffer, ref uint propertyBufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial uint CM_Get_DevNode_PropertyW(uint devInst, in DEVPROPKEY propertyKey, out uint propertyType, byte* propertyBuffer, ref uint propertyBufferSize, uint flags);

    /// <summary>Container id of the device that exposes the given interface path, or null when Windows cannot say.</summary>
    public static Guid? ContainerIdOf(string interfacePath)
    {
        const int Capacity = 2048;
        var buffer = stackalloc byte[Capacity];
        uint size = Capacity;
        if (CM_Get_Device_Interface_PropertyW(interfacePath, in DEVPKEY_Device_InstanceId, out _, buffer, ref size, 0) != 0)
        {
            return null;
        }
        var instanceId = Marshal.PtrToStringUni((nint)buffer);
        if (instanceId is null || CM_Locate_DevNodeW(out var devInst, instanceId, 0) != 0)
        {
            return null;
        }
        size = Capacity;
        if (CM_Get_DevNode_PropertyW(devInst, in DEVPKEY_Device_ContainerId, out _, buffer, ref size, 0) != 0 || size < 16)
        {
            return null;
        }
        return new Guid(new ReadOnlySpan<byte>(buffer, 16));
    }
}
