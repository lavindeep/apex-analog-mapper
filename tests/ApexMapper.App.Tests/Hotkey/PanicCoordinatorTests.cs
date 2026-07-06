using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ApexMapper.App.Services;
using ApexMapper.Core;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Hotkey;

public sealed class PanicCoordinatorTests
{
    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    private sealed class FakeHotkeyService : IHotkeyService
    {
        private readonly Dictionary<string, (HotkeyGesture gesture, Action callback)> _registrations = new();

        public IReadOnlyDictionary<string, (HotkeyGesture gesture, Action callback)> Registrations => _registrations;

        public void Register(string id, HotkeyGesture gesture, Action callback)
        {
            if (_registrations.ContainsKey(id))
                throw new InvalidOperationException($"Hotkey '{id}' is already registered.");
            _registrations[id] = (gesture, callback);
        }

        public void Unregister(string id) => _registrations.Remove(id);

        public bool IsRegistered(string id) => _registrations.ContainsKey(id);

        public void Dispose() { }

        public void FireHotkey(string id)
        {
            if (_registrations.TryGetValue(id, out var reg))
                reg.callback();
        }
    }

    private sealed class FakeSupervisorChannel : ISupervisorChannel
    {
        public int PanicCallCount { get; private set; }
        public Exception? ThrowOnPanic { get; set; }

        public bool IsConnected => true;

        public event EventHandler<SupervisorStatusEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SubmitPanicAsync(CancellationToken ct)
        {
            PanicCallCount++;
            if (ThrowOnPanic is not null) throw ThrowOnPanic;
            return Task.CompletedTask;
        }

        public Task SubmitControlAsync(ApexMapper.Core.Pipeline.VirtualPadState state, CancellationToken ct)
            => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class FakeForegroundWatcher : IForegroundWatcher
    {
        public ForegroundContext Current { get; set; } = ForegroundContext.Empty;

        public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged
        {
            add { }
            remove { }
        }

        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class FakePanicPolicyStore : IPanicPolicyStore
    {
        private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

        public List<string> DisabledCalls { get; } = new();

        public bool IsAutoEnableDisabled(string executablePath)
            => _disabled.Contains(executablePath);

        public void DisableAutoEnable(string executablePath)
        {
            DisabledCalls.Add(executablePath);
            _disabled.Add(executablePath);
        }

        public void EnableAutoEnable(string executablePath)
            => _disabled.Remove(executablePath);

        public IReadOnlyCollection<string> ListDisabled()
            => _disabled;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static readonly HotkeyGesture TestGesture =
        new(System.Windows.Input.Key.F12, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);

    private static (PanicCoordinator coordinator, FakeHotkeyService hotkey, FakeSupervisorChannel supervisor, FakeForegroundWatcher foreground, FakePanicPolicyStore policyStore)
        Build(string foregroundExe = @"C:\Games\Forza.exe")
    {
        var hotkey = new FakeHotkeyService();
        var supervisor = new FakeSupervisorChannel();
        var foreground = new FakeForegroundWatcher
        {
            Current = foregroundExe.Length == 0
                ? ForegroundContext.Empty
                : new ForegroundContext(foregroundExe, "Test Window", 1234u, null, DateTimeOffset.UtcNow)
        };
        var policyStore = new FakePanicPolicyStore();
        var coordinator = new PanicCoordinator(hotkey, supervisor, foreground, policyStore);
        return (coordinator, hotkey, supervisor, foreground, policyStore);
    }

    // ---------------------------------------------------------------------------
    // Registration tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void Start_registers_panic_hotkey()
    {
        var (coordinator, hotkey, _, _, _) = Build();

        coordinator.Start(TestGesture);

        hotkey.IsRegistered("panic").Should().BeTrue();
        hotkey.Registrations["panic"].gesture.Should().Be(TestGesture);
    }

    [Fact]
    public void Stop_unregisters_panic_hotkey()
    {
        var (coordinator, hotkey, _, _, _) = Build();
        coordinator.Start(TestGesture);

        coordinator.Stop();

        hotkey.IsRegistered("panic").Should().BeFalse();
    }

    // ---------------------------------------------------------------------------
    // PanicAsync tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PanicAsync_submits_panic_to_supervisor()
    {
        var (coordinator, _, supervisor, _, _) = Build();
        coordinator.Start(TestGesture);

        await coordinator.PanicAsync(CancellationToken.None);

        supervisor.PanicCallCount.Should().Be(1);
    }

    [Fact]
    public async Task PanicAsync_disables_auto_enable_for_current_foreground()
    {
        const string exe = @"C:\Games\Forza.exe";
        var (coordinator, _, _, _, policyStore) = Build(foregroundExe: exe);
        coordinator.Start(TestGesture);

        await coordinator.PanicAsync(CancellationToken.None);

        policyStore.DisabledCalls.Should().ContainSingle().Which.Should().Be(exe);
    }

    [Fact]
    public async Task PanicAsync_handles_empty_foreground_executable()
    {
        var (coordinator, _, supervisor, _, policyStore) = Build(foregroundExe: string.Empty);
        coordinator.Start(TestGesture);

        await coordinator.PanicAsync(CancellationToken.None);

        supervisor.PanicCallCount.Should().Be(1);
        policyStore.DisabledCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task PanicAsync_propagates_supervisor_failure_via_event_args()
    {
        const string exe = @"C:\Games\Forza.exe";
        var (coordinator, _, supervisor, _, policyStore) = Build(foregroundExe: exe);
        supervisor.ThrowOnPanic = new InvalidOperationException("simulated supervisor failure");
        coordinator.Start(TestGesture);

        PanicCompletedEventArgs? eventArgs = null;
        coordinator.PanicCompleted += (_, args) => eventArgs = args;

        await coordinator.PanicAsync(CancellationToken.None);

        eventArgs.Should().NotBeNull();
        eventArgs!.Error.Should().BeOfType<InvalidOperationException>();
        eventArgs.DisabledExecutablePath.Should().Be(exe);
        policyStore.DisabledCalls.Should().ContainSingle();
    }

    // ---------------------------------------------------------------------------
    // Hotkey callback test
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Hotkey_press_invokes_panic_asynchronously()
    {
        var (coordinator, hotkey, supervisor, _, _) = Build();
        coordinator.Start(TestGesture);

        var tcs = new TaskCompletionSource<PanicCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.PanicCompleted += (_, args) => tcs.TrySetResult(args);

        hotkey.FireHotkey("panic");

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        result.Should().NotBeNull();
        supervisor.PanicCallCount.Should().Be(1);
    }
}
