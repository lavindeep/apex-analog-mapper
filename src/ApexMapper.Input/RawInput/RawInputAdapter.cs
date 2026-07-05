using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Pipeline;
using ApexMapper.Input.Abstractions.RawInput;
using static ApexMapper.Input.RawInput.RawInputNative;

namespace ApexMapper.Input.RawInput;

public sealed class RawInputAdapter : IRawInputAdapter
{
    private readonly SpscRingBuffer<RawKeyEvent> _ring;
    private readonly object _lifecycleLock = new();

    // Touched only on the pump thread (WM_INPUT / WM_INPUT_DEVICE_CHANGE).
    private readonly RawInputDeviceIdMap _deviceIds = new();

    // Stateful decoder (Pause/Break lead-in tracking); pump thread only.
    private readonly RawInputMessageDecoder _decoder = new();

    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private int _started;
    private BackendStatus _status = BackendStatus.Stopped;

    public RawInputAdapter(SpscRingBuffer<RawKeyEvent> ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        _ring = ring;
    }

    public BackendStatus Status => _status;

    public event EventHandler<BackendStatusChanged>? StatusChanged;
    public event EventHandler<RawInputDeviceChanged>? DeviceChanged;

    public Task StartAsync(CancellationToken ct)
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return Task.CompletedTask;
            }

            SetStatus(BackendStatus.Starting, null);

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _pumpThread = new Thread(() => PumpThreadMain(ready))
            {
                IsBackground = true,
                Name = "ApexRawInputPump",
            };
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();

            try
            {
                ready.Task.Wait(ct);
            }
            catch (AggregateException ae) when (ae.InnerException is not null)
            {
                _started = 0;
                _pumpThread = null;
                SetStatus(BackendStatus.FaultedDigital, ae.InnerException.Message);
                throw ae.InnerException;
            }

            SetStatus(BackendStatus.Running, null);
            return Task.CompletedTask;
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        Thread? thread;
        uint threadId;
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _started, 0) == 0)
            {
                return Task.CompletedTask;
            }

            SetStatus(BackendStatus.Stopping, null);
            thread = _pumpThread;
            threadId = _pumpThreadId;
            _pumpThread = null;
        }

        if (thread is not null && threadId != 0)
        {
            PostThreadMessageW(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join();
        }

        SetStatus(BackendStatus.Stopped, null);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void SetStatus(BackendStatus status, string? reason)
    {
        _status = status;
        StatusChanged?.Invoke(this, new BackendStatusChanged(BackendKind.RawInput, status, reason));
    }

    private void PumpThreadMain(TaskCompletionSource<bool> ready)
    {
        HwndSource? source = null;
        var signalled = false;
        try
        {
            _pumpThreadId = GetCurrentThreadId();

            var parameters = new HwndSourceParameters("ApexAnalogRawInputSink")
            {
                ParentWindow = HWND_MESSAGE,
                WindowStyle = 0,
            };
            source = new HwndSource(parameters);
            source.AddHook(WndProc);

            var device = new RAWINPUTDEVICE
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_GENERIC_KEYBOARD,
                Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                Target = source.Handle,
            };
            var devices = new[] { device };
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"RegisterRawInputDevices failed with Win32 error {err}.");
            }

            signalled = true;
            ready.TrySetResult(true);

            Dispatcher.Run();

            var unregister = new RAWINPUTDEVICE
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_GENERIC_KEYBOARD,
                Flags = RIDEV_REMOVE,
                Target = IntPtr.Zero,
            };
            RegisterRawInputDevices(new[] { unregister }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch (Exception ex)
        {
            if (!signalled)
            {
                ready.TrySetException(ex);
            }
        }
        finally
        {
            try
            {
                source?.RemoveHook(WndProc);
                source?.Dispose();
            }
            catch
            {
            }

            _pumpThreadId = 0;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_INPUT:
                HandleRawInput(lParam);
                handled = true;
                return IntPtr.Zero;

            case WM_INPUT_DEVICE_CHANGE:
                HandleDeviceChange(wParam.ToInt32(), lParam);
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private unsafe void HandleRawInput(IntPtr hRawInput)
    {
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0)
        {
            return;
        }

        if (size == 0 || size > 1024)
        {
            return;
        }

        byte* p = stackalloc byte[(int)size];
        var got = GetRawInputData(hRawInput, RID_INPUT, (IntPtr)p, ref size, headerSize);
        if (got == unchecked((uint)-1) || got == 0)
        {
            return;
        }

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>((IntPtr)p);
        if (header.Type != RIM_TYPEKEYBOARD)
        {
            return;
        }

        var keyboardOffset = (int)headerSize;
        var keyboardLength = Marshal.SizeOf<RAWKEYBOARD>();
        if (keyboardOffset + keyboardLength > (int)size)
        {
            return;
        }

        var keyboardSpan = new ReadOnlySpan<byte>(p + keyboardOffset, keyboardLength);
        var deviceId = _deviceIds.GetOrAdd(header.Device);
        var timestamp = Stopwatch.GetTimestamp();
        if (_decoder.TryDecode(keyboardSpan, deviceId, timestamp, out var ev))
        {
            _ring.TryEnqueue(in ev);
        }
    }

    private void HandleDeviceChange(int change, IntPtr deviceHandle)
    {
        var attached = change == GIDC_ARRIVAL;
        var deviceId = attached
            ? _deviceIds.GetOrAdd(deviceHandle)
            : _deviceIds.Remove(deviceHandle);
        var path = TryGetDevicePath(deviceHandle);
        var identity = RawInputDevicePath.Parse(path);
        var devicePath = path ?? string.Empty;
        DeviceChanged?.Invoke(this, new RawInputDeviceChanged(identity, attached, devicePath, deviceId));
    }

    private static string? TryGetDevicePath(IntPtr deviceHandle)
    {
        uint size = 0;
        if (GetRawInputDeviceInfoW(deviceHandle, RIDI_DEVICENAME, IntPtr.Zero, ref size) != 0)
        {
            return null;
        }

        if (size == 0 || size > 4096)
        {
            return null;
        }

        var charCount = (int)size;
        var buffer = Marshal.AllocHGlobal(charCount * sizeof(char));
        try
        {
            var written = GetRawInputDeviceInfoW(deviceHandle, RIDI_DEVICENAME, buffer, ref size);
            if (written == unchecked((uint)-1) || written == 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

}
