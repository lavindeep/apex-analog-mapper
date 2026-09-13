using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ApexMapper.Core.Keys;

namespace ApexMapper.App.Services;

/// <summary>Suppresses mapped scan codes from all keyboards while one process is foreground.</summary>
public sealed class KeyboardSuppression : IKeyboardSuppression, IDisposable
{
    private readonly object _gate = new();
    private Worker? _active;
    private Worker? _applied;
    private bool _disposed;
    public event Action<string>? Faulted;
    public bool IsTargetForeground => Volatile.Read(ref _active)?.IsTargetForeground() == true;

    public IDisposable Enable(int processId, IReadOnlyCollection<KeyId> analogKeys, IReadOnlyCollection<KeyId> digitalKeys)
    {
        ArgumentNullException.ThrowIfNull(analogKeys);
        ArgumentNullException.ThrowIfNull(digitalKeys);
        Worker worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) throw new InvalidOperationException("Keyboard suppression is already active.");
            worker = new Worker(this, processId, analogKeys.ToArray(), digitalKeys.Except(analogKeys).ToArray());
            _active = worker;
        }
        try { worker.Start(); return worker; }
        catch { worker.Dispose(); throw; }
    }

    // Called only by the mapping tick, after Raw Input has drained.
    public void ApplyTo(KeyStateStore store)
    {
        var worker = Volatile.Read(ref _active);
        if (!ReferenceEquals(worker, _applied))
        {
            if (_applied is not null)
                foreach (var key in _applied.DigitalKeys)
                    if (store.Get(key).Source == KeyProvenance.Digital) store.Set(key, 0, KeyProvenance.Digital);
            _applied = worker;
        }
        if (worker is null) return;
        worker.ApplyTo(store);
        if (!ReferenceEquals(worker, Volatile.Read(ref _active))) store.GateHeldKeys(KeyProvenance.Digital);
    }

    public void Dispose()
    {
        Worker? worker;
        lock (_gate)
        {
            _disposed = true;
            worker = _active;
            _active = null;
        }
        worker?.Dispose();
    }

    private void Release(Worker worker)
    {
        Interlocked.CompareExchange(ref _active, null, worker);
    }

    private void ReportFault(Worker worker, string reason)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, worker)) return;
            try { Faulted?.Invoke(reason); } catch { /* Input remains unblocked if a subscriber fails. */ }
        }
    }

    private sealed class Worker : IDisposable
    {
        private readonly KeyboardSuppression _owner;
        private readonly Process _process;
        private readonly int _processId;
        private readonly KeyId[] _keys;
        public KeyId[] DigitalKeys { get; }
        private readonly KeyboardSuppressionPolicy _policy;
        private readonly Native.HookProc _callback;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint _hook;
        private int _enabled = 1;
        private volatile string? _failure;

        public Worker(KeyboardSuppression owner, int processId, KeyId[] analogKeys, KeyId[] digitalKeys)
        {
            _owner = owner;
            _process = Process.GetProcessById(processId);
            try
            {
                _ = _process.SafeHandle;
                if (_process.HasExited) throw new InvalidOperationException("The foreground process exited.");
            }
            catch { _process.Dispose(); throw; }
            _processId = processId;
            _keys = analogKeys.Concat(digitalKeys).Distinct().ToArray();
            DigitalKeys = digitalKeys;
            _policy = new(_keys, digitalKeys);
            _callback = OnKeyboard;
        }

        public void Start()
        {
            try { new Thread(Run) { IsBackground = true, Name = "KeyboardSuppression" }.Start(); }
            catch { _process.Dispose(); throw; }
            _ready.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _enabled, 0);
            _owner.Release(this);
        }

        public void ApplyTo(KeyStateStore store)
        {
            var generation = _policy.Generation;
            _policy.ApplyTo(store, IsTargetForeground());
            if (!IsTargetForeground() || generation != _policy.Generation)
                store.GateHeldKeys(KeyProvenance.Digital);
        }

        private void Run()
        {
            nuint timer = 0;
            try
            {
                if (Volatile.Read(ref _enabled) == 0) { _ready.TrySetCanceled(); return; }
                _hook = Native.SetWindowsHookEx(13, _callback, Native.GetModuleHandle(null), 0);
                if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Snapshot after installation so downs in the startup gap keep their ups.
                foreach (var key in _keys)
                {
                    var virtualKey = key.ScanCode == 0xE11D ? 0x13u : Native.MapVirtualKey(key.ScanCode, 3);
                    if (virtualKey != 0 && IsDown((int)virtualKey)) _policy.SetInitiallyHeld(key);
                }
                timer = Native.SetTimer(0, 1, 50, 0);
                if (timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (Volatile.Read(ref _enabled) == 0) { _ready.TrySetCanceled(); return; }
                _ready.TrySetResult();
                int result = 0;
                while (Volatile.Read(ref _enabled) != 0 && (result = Native.GetMessage(out var message, 0, 0, 0)) > 0)
                {
                    if (message.Message == 0x0113)
                    {
                        if (_process.HasExited) { _failure = "The target process exited."; break; }
                        _policy.SetActive(IsTargetForeground());
                    }
                    Native.TranslateMessage(in message);
                    Native.DispatchMessage(in message);
                }
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception error)
            {
                _failure = error.Message;
                _ready.TrySetException(error);
            }
            finally
            {
                var unexpected = Interlocked.Exchange(ref _enabled, 0) != 0;
                if (_hook != 0) Native.UnhookWindowsHookEx(_hook);
                if (timer != 0) Native.KillTimer(0, timer);
                _process.Dispose();
                if (unexpected && _ready.Task.IsCompletedSuccessfully)
                    _owner.ReportFault(this, _failure ?? "Keyboard suppression stopped unexpectedly.");
                _owner.Release(this);
                GC.KeepAlive(_callback);
            }
        }

        private nint OnKeyboard(int code, nuint message, nint data)
        {
            if (code >= 0 && Volatile.Read(ref _enabled) != 0)
            {
                try
                {
                    var key = Marshal.PtrToStructure<Native.KeyboardEvent>(data);
                    var down = message is 0x0100 or 0x0104;
                    var up = message is 0x0101 or 0x0105;
                    if (down || up)
                    {
                        _policy.SetActive(IsTargetForeground());
                        var scanCode = key.VirtualKey == 0x13 ? (ushort)0xE11D
                            : (ushort)(key.ScanCode | ((key.Flags & 1) != 0 ? 0xE000u : 0));
                        var shortcut = IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C) || (key.Flags & 0x20) != 0;
                        if (_policy.ShouldSuppress(new(scanCode), down, (key.Flags & 0x12) != 0, shortcut)
                            && Volatile.Read(ref _enabled) != 0)
                            return 1;
                    }
                }
                catch (Exception error)
                {
                    _failure = error.Message;
                    _policy.SetActive(false);
                    Native.PostQuitMessage(0);
                }
            }
            return Native.CallNextHookEx(_hook, code, message, data);
        }

        public bool IsTargetForeground()
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var processId);
            return Volatile.Read(ref _enabled) != 0 && _failure is null && processId == _processId;
        }
        private static bool IsDown(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;
    }

    private static class Native
    {
        internal delegate nint HookProc(int code, nuint message, nint data);
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardEvent { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct MessageData { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
        [DllImport("user32", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);
        [DllImport("user32")] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
        [DllImport("user32", SetLastError = true)] internal static extern int GetMessage(out MessageData message, nint window, uint min, uint max);
        [DllImport("user32")] internal static extern bool TranslateMessage(in MessageData message);
        [DllImport("user32")] internal static extern nint DispatchMessage(in MessageData message);
        [DllImport("user32")] internal static extern void PostQuitMessage(int code);
        [DllImport("user32", SetLastError = true)] internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
        [DllImport("user32")] internal static extern bool KillTimer(nint window, nuint id);
        [DllImport("user32")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32")] internal static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32")] internal static extern nint GetForegroundWindow();
        [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    }
}
