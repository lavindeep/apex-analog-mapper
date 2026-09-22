using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ApexMapper.Windows.Native;

namespace ApexMapper.Windows.Input;

/// <summary>
/// Raw Input on its own thread: a message-only window registered for keyboards with
/// <c>RIDEV_INPUTSINK</c> (events even when another window is foreground) and
/// <c>RIDEV_DEVNOTIFY</c> (arrival and removal). Key events go into a single-producer,
/// single-consumer ring; overflow is counted, never blocks. Device events are raised on
/// the pump thread. Raw Input only attributes events to a device; the hook owns the
/// digital state. One pump per process.
/// </summary>
public sealed unsafe class RawInputPump : IDisposable
{
    public const int RingSize = 256;
    private const string ClassName = "ApexAnalogMapper.RawInput";

    private static RawInputPump? s_current;

    private readonly RawKeyEvent[] _ring = new RawKeyEvent[RingSize];
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Lock _namesLock = new();
    private readonly Dictionary<nint, Guid> _containers = new();
    private int _head;
    private int _tail;
    private int _overflows;
    private int _handlerFaults;
    private Thread? _thread;
    private uint _threadId;
    private nint _window;
    private Exception? _startError;

    /// <summary>A keyboard arrived (true) or was removed (false). Raised on the pump thread; a handler that throws is counted, never propagated.</summary>
    public event Action<nint, bool>? DeviceChanged;

    public int Overflows => Volatile.Read(ref _overflows);

    /// <summary>Exceptions thrown by <see cref="DeviceChanged"/> handlers; an exception must never leave the window procedure.</summary>
    public int HandlerFaults => Volatile.Read(ref _handlerFaults);

    public bool IsRunning => _thread is { IsAlive: true } && _window != 0;

    /// <summary>Starts the thread and blocks until the window and registration exist. Throws when Windows refuses.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The pump was already started.");
        }
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
        {
            throw new InvalidOperationException("Only one Raw Input pump can exist per process.");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "apex-rawinput" };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
        {
            _thread.Join();
            Interlocked.Exchange(ref s_current, null);
            throw _startError;
        }
    }

    /// <summary>Posts quit and joins.</summary>
    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }
        User32.PostThreadMessageW(_threadId, User32.WM_QUIT, 0, 0);
        _thread.Join();
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    /// <summary>Consumer side. Allocation-free.</summary>
    public bool TryDequeue(out RawKeyEvent item)
    {
        var tail = Volatile.Read(ref _tail);
        if (tail == Volatile.Read(ref _head))
        {
            item = default;
            return false;
        }
        item = _ring[tail % RingSize];
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    /// <summary>
    /// The container id of a Raw Input device handle, or null when Windows cannot say.
    /// Successful lookups are cached until the handle is reported removed or re-added,
    /// since Windows reuses handle values across a replug; failures are not cached, so a
    /// lookup that races an arrival is retried. Not for the hot path.
    /// </summary>
    public Guid? ContainerIdOf(nint device)
    {
        lock (_namesLock)
        {
            if (_containers.TryGetValue(device, out var cached))
            {
                return cached;
            }
            var name = DeviceName(device);
            var container = name is null ? null : CfgMgr32.ContainerIdOf(name);
            if (container is { } found)
            {
                _containers[device] = found;
            }
            return container;
        }
    }

    private void Forget(nint device)
    {
        lock (_namesLock)
        {
            _containers.Remove(device);
        }
    }

    internal bool IsCached(nint device)
    {
        lock (_namesLock)
        {
            return _containers.ContainsKey(device);
        }
    }

    internal void CacheForTest(nint device, Guid container)
    {
        lock (_namesLock)
        {
            _containers[device] = container;
        }
    }

    /// <summary>The device interface path of a Raw Input device handle.</summary>
    public static string? DeviceName(nint device)
    {
        uint size = 0;
        if (User32.GetRawInputDeviceInfoW(device, User32.RIDI_DEVICENAME, null, &size) != 0 || size == 0)
        {
            return null;
        }
        var buffer = stackalloc char[(int)size + 1];
        var written = User32.GetRawInputDeviceInfoW(device, User32.RIDI_DEVICENAME, buffer, &size);
        return written is 0 or uint.MaxValue ? null : new string(buffer, 0, (int)written).TrimEnd('\0');
    }

    internal void Enqueue(in RawKeyEvent item)
    {
        var head = _head;
        if (head - Volatile.Read(ref _tail) >= RingSize)
        {
            Interlocked.Increment(ref _overflows);
            return;
        }
        _ring[head % RingSize] = item;
        Volatile.Write(ref _head, head + 1);
    }

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        var instance = Kernel32.GetModuleHandleW(null);
        ushort atom = 0;
        try
        {
            fixed (char* className = ClassName)
            {
                var wc = new User32.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(User32.WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = instance,
                    lpszClassName = className,
                };
                atom = User32.RegisterClassExW(in wc);
            }
            if (atom == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");
            }
            _window = User32.CreateWindowExW(0, ClassName, string.Empty, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, instance, 0);
            if (_window == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");
            }
            var device = new User32.RAWINPUTDEVICE
            {
                usUsagePage = User32.HID_USAGE_PAGE_GENERIC,
                usUsage = User32.HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = User32.RIDEV_INPUTSINK | User32.RIDEV_DEVNOTIFY,
                hwndTarget = _window,
            };
            if (!User32.RegisterRawInputDevices(&device, 1, (uint)sizeof(User32.RAWINPUTDEVICE)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed.");
            }
        }
        catch (Exception e)
        {
            _startError = e;
            Cleanup(atom, instance);
            _ready.Set();
            return;
        }
        _ready.Set();
        while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }
        Cleanup(atom, instance);
        Interlocked.Exchange(ref s_current, null);
    }

    private void Cleanup(ushort atom, nint instance)
    {
        if (_window != 0)
        {
            User32.DestroyWindow(_window);
            _window = 0;
        }
        if (atom != 0)
        {
            User32.UnregisterClassW(ClassName, instance);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        var self = s_current;
        if (self is null)
        {
            return User32.DefWindowProcW(hwnd, message, wParam, lParam);
        }
        switch (message)
        {
            case User32.WM_INPUT:
                self.OnInput(lParam);
                return User32.DefWindowProcW(hwnd, message, wParam, lParam);
            case User32.WM_INPUT_DEVICE_CHANGE:
                self.OnDeviceChanged(lParam, wParam == User32.GIDC_ARRIVAL);
                return 0;
            default:
                return User32.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    internal void OnDeviceChanged(nint device, bool arrived)
    {
        Forget(device);
        try
        {
            DeviceChanged?.Invoke(device, arrived);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }

    private void OnInput(nint handle)
    {
        User32.RAWINPUTKEYBOARD data;
        var size = (uint)sizeof(User32.RAWINPUTKEYBOARD);
        var read = User32.GetRawInputData(handle, User32.RID_INPUT, &data, &size, (uint)sizeof(User32.RAWINPUTHEADER));
        if (read == uint.MaxValue || read < sizeof(User32.RAWINPUTKEYBOARD) || data.header.dwType != User32.RIM_TYPEKEYBOARD)
        {
            return;
        }
        if (RawInputDecoder.TryDecode(data.keyboard.MakeCode, data.keyboard.Flags, out var code, out var down))
        {
            Enqueue(new RawKeyEvent(code, down, data.header.hDevice, Stopwatch.GetTimestamp()));
        }
    }
}
