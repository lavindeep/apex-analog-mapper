using System.Runtime.InteropServices;
using System.Text;

namespace ApexMapper.Input.RawInput;

/// <summary>Reads presentation metadata without opening or changing the keyboard.</summary>
internal static class WindowsKeyboardMetadata
{
    private static readonly PropertyKey InstanceId = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);
    private static readonly PropertyKey ContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    private static readonly PropertyKey BusDescription = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    private static readonly PropertyKey FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);

    public static (string? DisplayName, string? PhysicalDeviceId) Read(string path)
    {
        var buffer = new byte[4096];
        uint size = (uint)buffer.Length;
        if (CM_Get_Device_Interface_PropertyW(path, in InstanceId, out var type, buffer, ref size, 0) != 0
            || type != 18 || size > buffer.Length)
            return (null, null);
        var instance = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        if (CM_Locate_DevNodeW(out var node, instance, 0) != 0)
            return (null, null);

        var container = ReadContainer(node);
        string? name = null;
        // Composite keyboards expose several HID nodes. Only inspect ancestors
        // in this container, so a hub or computer name can never label the keyboard.
        for (var depth = 0; depth < 8; depth++)
        {
            name = ReadString(node, in BusDescription) ?? name ?? ReadString(node, in FriendlyName);
            if (container is null || CM_Get_Parent(out var parent, node, 0) != 0
                || ReadContainer(parent) != container)
                break;
            node = parent;
        }
        return (name, container?.ToString("D"));
    }

    private static Guid? ReadContainer(uint node)
    {
        var buffer = new byte[16];
        uint size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(node, in ContainerId, out var type, buffer, ref size, 0) != 0
            || type != 13 || size != 16)
            return null;
        var id = new Guid(buffer);
        return id == Guid.Empty ? null : id;
    }

    private static string? ReadString(uint node, in PropertyKey key)
    {
        var buffer = new byte[4096];
        uint size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(node, in key, out var type, buffer, ref size, 0) != 0
            || type != 18 || size > buffer.Length)
            return null;
        var value = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
        return value.Length == 0 ? null : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(string formatId, uint propertyId)
    {
        public readonly Guid FormatId = new(formatId);
        public readonly uint PropertyId = propertyId;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string deviceInterface, in PropertyKey key,
        out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, in PropertyKey key,
        out uint type, [Out] byte[] buffer, ref uint size, uint flags);
}
