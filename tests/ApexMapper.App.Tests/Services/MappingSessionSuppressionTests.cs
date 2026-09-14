using ApexMapper.App.Services;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Output.Detection;
using ApexMapper.Output.Preflight;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApexMapper.App.Tests.Services;

public sealed class MappingSessionSuppressionTests
{
    private static readonly KeyId Throttle = KeyId.FromScanCode(0x11);
    private static readonly KeyId Steer = KeyId.FromScanCode(0x1e);
    private static readonly KeyId Handbrake = KeyId.FromScanCode(0x39);
    private static readonly KeyId Clutch = KeyId.FromScanCode(0x2a);
    private static readonly KeyId ShiftDown = KeyId.FromScanCode(0x10);
    private static readonly KeyId ShiftUp = KeyId.FromScanCode(0x12);
    private static readonly GameProcess Game = new(42, 1234, "Test game", "game.exe");

    [Fact]
    public async Task Imported_reserved_binding_blocks_start_before_controller_or_filter_side_effects()
    {
        using var h = new Harness { MappedKeys = new[] { Throttle, new KeyId(0xE01D) } };

        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeFalse();

        h.Launcher.Calls.Should().Be(0);
        h.Channel.ConnectCalls.Should().Be(0);
        h.Suppression.EnableCalls.Should().Be(0);
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.States.Last().Message.Should().Be("Ctrl, Alt, Windows and F12 cannot be mapped. Choose another key.");
    }

    [Fact]
    public async Task Start_uses_the_selected_process_and_separates_analog_from_digital_bindings()
    {
        using var h = new Harness();
        h.Suppression.OnEnable = () =>
        {
            h.Channel.IsConnected.Should().BeTrue();
            h.Engine.IsEnabled.Should().BeFalse();
        };

        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeTrue();

        h.Suppression.ProcessId.Should().Be(Game.ProcessId);
        h.Suppression.AnalogKeys.Should().Equal(Throttle, Steer);
        h.Suppression.DigitalKeys.Should().Equal(Handbrake, Clutch, ShiftDown, ShiftUp);
        h.Suppression.Lease.DisposeCalls.Should().Be(0);
        h.Session.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("exited")]
    [InlineData("reused")]
    public async Task Start_requires_a_selected_live_process_identity(string invalid)
    {
        using var h = new Harness();
        if (invalid == "none") h.Games.Select(null);
        if (invalid == "exited") h.ProcessStart = null;
        if (invalid == "reused") h.ProcessStart++;

        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeFalse();

        h.Channel.ConnectCalls.Should().Be(0);
        h.Suppression.EnableCalls.Should().Be(0);
        h.States.Last().Message.Should().Be("Choose a running game before starting.");
        h.Engine.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Input_failure_never_suppresses_keys_or_starts_the_controller()
    {
        using var h = new Harness { Readiness = "Calibrate keys before starting." };

        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeFalse();

        h.Suppression.EnableCalls.Should().Be(0);
        h.Channel.ConnectCalls.Should().Be(0);
        h.States.Last().Message.Should().Be(h.Readiness);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task Background_connect_must_be_ready_before_suppression_starts(string outcome)
    {
        using var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        h.Channel.ConnectImmediately = false;

        var start = h.Session.EnableAsync(cancellation.Token);
        start.IsCompleted.Should().BeFalse();
        h.Suppression.EnableCalls.Should().Be(0);
        h.Engine.IsEnabled.Should().BeFalse();

        if (outcome == "ready")
        {
            h.Channel.SetConnected(true);
            (await start.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeTrue();
            h.Suppression.EnableCalls.Should().Be(1);
            return;
        }
        if (outcome == "cancel")
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        }
        else
        {
            (await start.WaitAsync(TimeSpan.FromSeconds(8))).Should().BeFalse();
            h.States.Last().Message.Should().Contain("Controller did not connect");
        }
        h.Suppression.EnableCalls.Should().Be(0);
        h.Channel.DisconnectCalls.Should().Be(1);
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelled_Stop_still_releases_the_filter_synchronously()
    {
        using var h = new Harness();
        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Session.DisableAsync(cancellation.Token));

        h.Suppression.Lease.DisposeCalls.Should().Be(1);
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_releases_filter_and_gates_keys_before_slow_channel_cleanup()
    {
        using var h = new Harness();
        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeTrue();
        h.Store.Set(Throttle, .5f, KeyProvenance.Analog);
        var releaseDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Channel.OnDisconnect = () => releaseDisconnect.Task;

        var stop = h.Session.DisableAsync(CancellationToken.None);

        stop.IsCompleted.Should().BeFalse();
        h.Suppression.Lease.DisposeCalls.Should().Be(1);
        h.Engine.IsEnabled.Should().BeFalse();
        h.Store.IsGated(Throttle).Should().BeTrue();
        h.Store.Get(Throttle).Value.Should().Be(0);
        releaseDisconnect.SetResult();
        await stop;
        await h.Session.DisableAsync(CancellationToken.None);
        h.Suppression.Lease.DisposeCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("panic")]
    [InlineData("profile")]
    [InlineData("editor")]
    [InlineData("selection")]
    [InlineData("fault")]
    [InlineData("exit")]
    [InlineData("disconnect")]
    [InlineData("shutdown")]
    public async Task Every_off_path_releases_the_filter_and_never_restarts_itself(string cause)
    {
        using var h = new Harness();
        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeTrue();
        h.Store.Set(Throttle, .5f, KeyProvenance.Analog);

        switch (cause)
        {
            case "panic": h.Session.ForceLocalOff("Panic."); break;
            case "profile": h.Session.ForceLocalOff("Profile changed."); break;
            case "editor": h.Session.BlockForInputEditing().Dispose(); break;
            case "selection": h.Games.Select(Game with { ProcessId = 99 }); break;
            case "fault": h.Suppression.Fail("Keyboard filter stopped."); break;
            case "exit": h.Suppression.Fail("Selected game exited."); break;
            case "disconnect": h.Channel.SetConnected(false); break;
            case "shutdown": h.Session.Dispose(); break;
        }

        h.Suppression.Lease.DisposeCalls.Should().Be(1);
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.Store.Get(Throttle).Value.Should().Be(0);
        h.Store.IsGated(Throttle).Should().BeTrue();
        h.Channel.SetConnected(true);
        h.Session.IsEnabled.Should().BeFalse();
        h.Suppression.EnableCalls.Should().Be(1);
        h.Session.ForceLocalOff("Repeated Stop.");
        h.Suppression.Lease.DisposeCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("panic")]
    [InlineData("cancel")]
    [InlineData("selection")]
    [InlineData("fault")]
    [InlineData("disconnect")]
    [InlineData("shutdown")]
    public async Task Off_during_native_startup_disposes_the_late_lease_without_arming(string cause)
    {
        using var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var filtering = 0;
        h.Suppression.OnEnable = () =>
        {
            Volatile.Write(ref filtering, 1);
            using var registration = h.Suppression.StartupCancellation.Register(() => Volatile.Write(ref filtering, 0));
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        };
        var start = h.Session.EnableAsync(cancellation.Token);
        entered.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
        Volatile.Read(ref filtering).Should().Be(1);

        Task stop = Task.CompletedTask;
        switch (cause)
        {
            case "stop": stop = h.Session.DisableAsync(CancellationToken.None); break;
            case "panic": h.Session.ForceLocalOff("Panic."); break;
            case "cancel": cancellation.Cancel(); break;
            case "selection": h.Games.Select(Game with { ProcessId = 99 }); break;
            case "fault": h.Suppression.Fail("Keyboard filter failed."); break;
            case "disconnect": h.Channel.SetConnected(false); h.Channel.SetConnected(true); break;
            case "shutdown": h.Session.Dispose(); break;
        }
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        Volatile.Read(ref filtering).Should().Be(0, "Stop must reach the native filter before Enable returns its lease");
        release.Set();

        if (cause == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        else (await start.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeFalse();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        h.Suppression.Lease.DisposeCalls.Should().Be(1);
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.Channel.IsConnected.Should().BeFalse();
        h.States.Should().OnlyContain(state => !state.IsEnabled);
    }

    [Fact]
    public async Task Stop_before_native_cancellation_is_published_prevents_filter_startup()
    {
        using var h = new Harness();
        var reads = 0;
        h.OnReadMappedKeys = () =>
        {
            if (++reads == 2) h.Session.ForceLocalOff("Stop before native startup.");
        };

        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeFalse();

        h.Suppression.EnableCalls.Should().Be(0);
        h.Session.IsEnabled.Should().BeFalse();
        h.Channel.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task A_previous_Start_token_cannot_cancel_a_later_session()
    {
        using var h = new Harness();
        using var firstStart = new CancellationTokenSource();
        (await h.Session.EnableAsync(firstStart.Token)).Should().BeTrue();
        await h.Session.DisableAsync(CancellationToken.None);
        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeTrue();

        firstStart.Cancel();

        h.Session.IsEnabled.Should().BeTrue();
        h.Engine.IsEnabled.Should().BeTrue();
        h.Suppression.Lease.DisposeCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("exited")]
    [InlineData("reused")]
    public async Task Readiness_and_identity_are_rechecked_after_native_startup(string failure)
    {
        using var h = new Harness();
        h.Suppression.OnEnable = () =>
        {
            if (failure == "input") h.Readiness = "Keyboard disconnected.";
            if (failure == "exited") h.ProcessStart = null;
            if (failure == "reused") h.ProcessStart++;
        };

        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeFalse();

        h.Suppression.Lease.DisposeCalls.Should().Be(1);
        h.Engine.IsEnabled.Should().BeFalse();
        h.Channel.IsConnected.Should().BeFalse();
        h.States.Should().OnlyContain(state => !state.IsEnabled);
    }

    [Fact]
    public async Task Failed_native_startup_leaves_controller_and_engine_off()
    {
        using var h = new Harness();
        h.Suppression.OnEnable = () => throw new InvalidOperationException("Filter unavailable.");

        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeFalse();

        h.Engine.IsEnabled.Should().BeFalse();
        h.Channel.IsConnected.Should().BeFalse();
        h.States.Last().Message.Should().Contain("Filter unavailable.");
    }

    private sealed class Harness : IDisposable
    {
        public KeyStateStore Store { get; } = new(new KeyIndex(new[] { Throttle, Steer }));
        public FakeChannel Channel { get; } = new();
        public FakeSuppression Suppression { get; } = new();
        public Launcher Launcher { get; } = new();
        public IReadOnlyCollection<KeyId> MappedKeys { get; set; } =
            new[] { Throttle, Steer, Handbrake, Clutch, ShiftDown, ShiftUp };
        public Action? OnReadMappedKeys { get; set; }
        public List<MappingSessionStateChangedEventArgs> States { get; } = new();
        public long? ProcessStart { get; set; } = Game.ProcessStartTimeUtcTicks;
        public string? Readiness { get; set; }
        public GameSelection Games { get; }
        public MappingEngine Engine { get; }
        public MappingSession Session { get; }

        public Harness()
        {
            Games = new GameSelection(id => id == Game.ProcessId ? ProcessStart : null);
            Games.Select(Game);
            Engine = new MappingEngine(Store, new NullSink());
            Engine.SetEnabled(false);
            var processes = new NoProcesses();
            Session = new MappingSession(Store, Engine, Channel,
                new PreflightRunner(Array.Empty<IPreflightCheck>()),
                new AntiCheatDetector(processes), new SteamDetector(processes, Array.Empty<string>()),
                Launcher, new NoForeground(), (_, _) => true, NullLogger<MappingSession>.Instance,
                () => Readiness, Games, Suppression, () => new[] { Throttle, Steer },
                () => { OnReadMappedKeys?.Invoke(); return MappedKeys; });
            Session.CompleteInputStartup();
            Session.StateChanged += (_, state) => States.Add(state);
        }

        public void Dispose()
        {
            Session.Dispose();
            Engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class FakeChannel : ISupervisorChannel
    {
        public bool IsConnected { get; private set; }
        public bool ConnectImmediately { get; set; } = true;
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public Func<Task>? OnDisconnect { get; set; }
        public event EventHandler<SupervisorStatusEventArgs>? StatusChanged;
        public void SetConnected(bool value)
        {
            IsConnected = value;
            StatusChanged?.Invoke(this, new SupervisorStatusEventArgs(value, null));
        }
        public Task ConnectAsync(CancellationToken ct)
        {
            ConnectCalls++;
            if (ConnectImmediately) SetConnected(true);
            return Task.CompletedTask;
        }
        public async Task DisconnectAsync(CancellationToken ct)
        {
            DisconnectCalls++;
            if (OnDisconnect is not null) await OnDisconnect();
            SetConnected(false);
        }
        public Task SubmitPanicAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SubmitControlAsync(VirtualPadState state, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeSuppression : IKeyboardSuppression
    {
        public int EnableCalls { get; private set; }
        public int ProcessId { get; private set; }
        public IReadOnlyCollection<KeyId> AnalogKeys { get; private set; } = Array.Empty<KeyId>();
        public IReadOnlyCollection<KeyId> DigitalKeys { get; private set; } = Array.Empty<KeyId>();
        public bool IsTargetForeground => true;
        public TrackingLease Lease { get; private set; } = new();
        public CancellationToken StartupCancellation { get; private set; }
        public Action? OnEnable { get; set; }
        public event Action<string>? Faulted;
        public void Fail(string error) => Faulted?.Invoke(error);
        public IDisposable Enable(int processId, IReadOnlyCollection<KeyId> analogKeys, IReadOnlyCollection<KeyId> digitalKeys,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCalls++;
            ProcessId = processId;
            AnalogKeys = analogKeys;
            DigitalKeys = digitalKeys;
            Lease = new TrackingLease();
            StartupCancellation = cancellationToken;
            OnEnable?.Invoke();
            return Lease;
        }
        public void ApplyTo(KeyStateStore store) { }
    }

    private sealed class TrackingLease : IDisposable
    {
        private int _disposeCalls;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }

    private sealed class NullSink : IPadStateSink { public void Push(in VirtualPadState state) { } }
    private sealed class Launcher : ISupervisorProcessLauncher
    {
        public int Calls { get; private set; }
        public string? EnsureRunning() { Calls++; return null; }
    }
    private sealed class NoProcesses : IProcessEnumerator
    {
        public IReadOnlyList<ProcessSnapshot> Enumerate() => Array.Empty<ProcessSnapshot>();
        public ProcessSnapshot? GetById(int processId) => null;
    }
    private sealed class NoForeground : IForegroundWatcher
    {
        public ApexMapper.Core.ForegroundContext Current => ApexMapper.Core.ForegroundContext.Empty;
        public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
