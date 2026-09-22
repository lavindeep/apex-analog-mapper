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
/// the pump thread, and only for changes after registration: a consumer enumerates
/// first, then watches. Raw Input only attributes events to a device; the hook owns
/// the digital state. One pump per process.
///
/// Exceptions never leave the window procedure: a throwing <see cref="DeviceChanged"/>
/// handler, or anything thrown while reading an event, is counted in
/// <see cref="HandlerFaults"/>. <see cref="EventCount"/> and <see cref="LastEventTicks"/>
/// let the session compare this pump against the hook: raw events flowing while the
/// hook's counter stands still means Windows removed the hook.
/// </summary>
public sealed unsafe class RawInputPump : IDisposable
{
    public const int RingSize = 256;

    /// <summary>How long <see cref="Stop"/> waits for the pump thread before giving up on it.</summary>
    public const int JoinTimeoutMs = 2000;

    /// <summary>Events from every window plus device notifications; the two flags the design depends on.</summary>
    public const uint RegistrationFlags = User32.RIDEV_INPUTSINK | User32.RIDEV_DEVNOTIFY;

    private const string ClassName = "ApexAnalogMapper.RawInput";

    private static RawInputPump? s_current;

    private readonly RawKeyEvent[] _ring = new RawKeyEvent[RingSize];
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Lock _namesLock = new();
    private readonly Dictionary<nint, Guid> _containers = new();
    private int _cacheGeneration;
    private int _head;
    private int _tail;
    private int _overflows;
    private int _handlerFaults;
    private int _malformedInputs;
    private long _eventCount;
    private long _lastEventTicks;
    private Thread? _thread;
    private uint _threadId;
    private nint _window;
    private Exception? _error;

    /// <summary>A keyboard arrived (true) or was removed (false). Raised on the pump thread; a handler that throws is counted, never propagated.</summary>
    public event Action<nint, bool>? DeviceChanged;

    public int Overflows => Volatile.Read(ref _overflows);

    /// <summary>Exceptions caught on the pump thread: <see cref="DeviceChanged"/> handlers and the input path.</summary>
    public int HandlerFaults => Volatile.Read(ref _handlerFaults);

    /// <summary>WM_INPUT messages whose data could not be read or was not a keyboard event of the expected size.</summary>
    public int MalformedInputs => Volatile.Read(ref _malformedInputs);

    /// <summary>Decoded keyboard events since start, enqueued or overflowed.</summary>
    public long EventCount => Volatile.Read(ref _eventCount);

    /// <summary>Stopwatch timestamp of the last decoded keyboard event, or zero.</summary>
    public long LastEventTicks => Volatile.Read(ref _lastEventTicks);

    /// <summary>What ended the pump thread early, if anything did; null while it runs or after a clean stop.</summary>
    public Exception? Error => Volatile.Read(ref _error);

    public bool IsRunning => _thread is { IsAlive: true } && Volatile.Read(ref _window) != 0;

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
        if (_error is not null)
        {
            _thread.Join();
            throw _error;
        }
    }

    /// <summary>
    /// Posts quit; the window is destroyed on the pump thread. From any other thread
    /// this joins, bounded by <see cref="JoinTimeoutMs"/>, and returns whether the
    /// thread has exited. From the pump thread itself (a <see cref="DeviceChanged"/>
    /// handler) it returns false at once and the thread exits after the current message.
    /// The container cache is cleared, since handle values do not survive a stop.
    /// </summary>
    public bool Stop()
    {
        var thread = _thread;
        if (thread is null || !thread.IsAlive)
        {
            return true;
        }
        for (var attempt = 0; attempt < 20 && !User32.PostThreadMessageW(_threadId, User32.WM_QUIT, 0, 0); attempt++)
        {
            Thread.Sleep(5);
        }
        if (thread.ManagedThreadId == Environment.CurrentManagedThreadId)
        {
            return false;
        }
        var exited = thread.Join(JoinTimeoutMs);
        lock (_namesLock)
        {
            _containers.Clear();
            _cacheGeneration++;
        }
        return exited;
    }

    public void Dispose()
    {
        if (Stop())
        {
            _ready.Dispose();
        }
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
        item = _ring[tail & (RingSize - 1)];
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    /// <summary>
    /// The container id of a Raw Input device handle, or null when Windows cannot say.
    /// Successful lookups are cached until the handle is reported removed or re-added,
    /// since Windows reuses handle values across a replug; failures are not cached, so a
    /// lookup that races an arrival is retried. The lookup itself runs outside the lock:
    /// it calls cfgmgr32, which the pump thread also calls. A device change during the
    /// lookup bumps the cache generation, and a result from the old generation is
    /// returned but not cached. Not for the hot path.
    /// </summary>
    public Guid? ContainerIdOf(nint device) => ContainerIdOf(device, Lookup);

    internal Guid? ContainerIdOf(nint device, Func<nint, Guid?> lookup)
    {
        int generation;
        lock (_namesLock)
        {
            if (_containers.TryGetValue(device, out var cached))
            {
                return cached;
            }
            generation = _cacheGeneration;
        }
        var container = lookup(device);
        if (container is { } found)
        {
            lock (_namesLock)
            {
                if (generation == _cacheGeneration)
                {
                    _containers[device] = found;
                }
            }
        }
        return container;
    }

    private static Guid? Lookup(nint device)
    {
        var name = DeviceName(device);
        return name is null ? null : CfgMgr32.ContainerIdOf(name);
    }

    private void Forget(nint device)
    {
        lock (_namesLock)
        {
            _containers.Remove(device);
            _cacheGeneration++;
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
        Volatile.Write(ref _lastEventTicks, item.Ticks);
        Volatile.Write(ref _eventCount, _eventCount + 1);
        var head = _head;
        if (head - Volatile.Read(ref _tail) >= RingSize)
        {
            Interlocked.Increment(ref _overflows);
            return;
        }
        _ring[head & (RingSize - 1)] = item;
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
            var window = User32.CreateWindowExW(0, ClassName, string.Empty, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, instance, 0);
            if (window == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");
            }
            Volatile.Write(ref _window, window);
            var device = new User32.RAWINPUTDEVICE
            {
                usUsagePage = User32.HID_USAGE_PAGE_GENERIC,
                usUsage = User32.HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RegistrationFlags,
                hwndTarget = window,
            };
            if (!User32.RegisterRawInputDevices(&device, 1, (uint)sizeof(User32.RAWINPUTDEVICE)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed.");
            }
            _ready.Set();
            while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _error, e);
        }
        finally
        {
            Cleanup(atom, instance);
            Interlocked.CompareExchange(ref s_current, null, this);
            _ready.Set();
        }
    }

    private void Cleanup(ushort atom, nint instance)
    {
        var window = _window;
        if (window != 0)
        {
            User32.DestroyWindow(window);
            Volatile.Write(ref _window, 0);
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
        try
        {
            User32.RAWINPUTKEYBOARD data;
            var size = (uint)sizeof(User32.RAWINPUTKEYBOARD);
            var read = User32.GetRawInputData(handle, User32.RID_INPUT, &data, &size, (uint)sizeof(User32.RAWINPUTHEADER));
            if (read == uint.MaxValue || read < sizeof(User32.RAWINPUTKEYBOARD) || data.header.dwType != User32.RIM_TYPEKEYBOARD || data.header.dwSize != read)
            {
                Interlocked.Increment(ref _malformedInputs);
                return;
            }
            if (RawInputDecoder.TryDecode(data.keyboard.MakeCode, data.keyboard.Flags, out var code, out var down))
            {
                Enqueue(new RawKeyEvent(code, down, data.header.hDevice, Stopwatch.GetTimestamp()));
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerFaults);
        }
    }
}
