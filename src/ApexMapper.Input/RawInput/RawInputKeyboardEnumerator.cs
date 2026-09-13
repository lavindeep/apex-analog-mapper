using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.RawInput;
using static ApexMapper.Input.RawInput.RawInputNative;

namespace ApexMapper.Input.RawInput;

/// <summary>Discovers SteelSeries digital keyboard sources using the exact paths emitted by Raw Input.</summary>
public sealed class RawInputKeyboardEnumerator : IDeviceEnumerator
{
    public IReadOnlyList<DiscoveredDevice> Enumerate()
    {
        var devices = new List<DiscoveredDevice>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, path) in EnumerateKeyboardPaths())
        {
            var identity = RawInputDevicePath.Parse(path);
            if (identity.VendorId == 0x1038 && identity.ProductId != 0 && paths.Add(path))
                devices.Add(new DiscoveredDevice(identity, path, SupportsAnalog: false));
        }
        return devices;
    }

    private static IReadOnlyList<(IntPtr Handle, string Path)> EnumerateKeyboardPaths()
    {
        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        // Device arrival can grow the list between the size query and read.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            if (GetRawInputDeviceList(IntPtr.Zero, ref count, size) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count == 0) return Array.Empty<(IntPtr, string)>();
            var buffer = Marshal.AllocHGlobal(checked((int)(count * size)));
            try
            {
                var read = GetRawInputDeviceList(buffer, ref count, size);
                if (read == uint.MaxValue)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 122) continue;
                    throw new Win32Exception(error);
                }
                var result = new List<(IntPtr, string)>();
                for (var i = 0; i < read; i++)
                {
                    var item = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buffer + checked((int)(i * size)));
                    if (item.Type == RIM_TYPEKEYBOARD && RawInputAdapter.TryGetDevicePath(item.Device) is { } path)
                        result.Add((item.Device, path));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Raw Input device list kept changing; refresh devices to retry.");
    }

}
