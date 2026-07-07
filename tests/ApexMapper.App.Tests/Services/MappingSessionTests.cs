using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

public sealed class MappingSessionTests
{
    private static readonly KeyId Throttle = KeyId.FromScanCode(0x11);

    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    private sealed class NullSink : IPadStateSink
    {
        public void Push(in VirtualPadState state) { }
    }

    private sealed class FakeChannel : ISupervisorChannel
    {
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int PanicCalls { get; private set; }
        public Exception? ThrowOnDisconnect { get; set; }

        /// <summary>Invoked at the moment DisconnectAsync is entered — lets a
        /// test snapshot engine/store state to prove the local-off ran first.</summary>
        public Action? OnDisconnectEntered { get; set; }

        public bool IsConnected => ConnectCalls > DisconnectCalls;

        public event EventHandler<SupervisorStatusEventArgs>? StatusChanged;

        public void RaiseStatus(bool connected, string? error)
            => StatusChanged?.Invoke(this, new SupervisorStatusEventArgs(connected, error));

        public Task ConnectAsync(CancellationToken ct)
        {
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public Task SubmitPanicAsync(CancellationToken ct)
        {
            PanicCalls++;
            return Task.CompletedTask;
        }

        public Task SubmitControlAsync(VirtualPadState state, CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct)
        {
            OnDisconnectEntered?.Invoke();
            DisconnectCalls++;
            if (ThrowOnDisconnect is not null) throw ThrowOnDisconnect;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakePreflightCheck(string id, PreflightIssue? result) : IPreflightCheck
    {
        public int Runs { get; private set; }

        public string CheckId => id;

        public PreflightIssue? Run()
        {
            Runs++;
            return result;
        }
    }

    private sealed class FakeProcessEnumerator : IProcessEnumerator
    {
        public List<ProcessSnapshot> Processes { get; } = new();
        public Exception? ThrowOnEnumerate { get; set; }

        public IReadOnlyList<ProcessSnapshot> Enumerate()
        {
            if (ThrowOnEnumerate is not null) throw ThrowOnEnumerate;
            return Processes;
        }

        public ProcessSnapshot? GetById(int processId)
            => Enumerate().FirstOrDefault(p => p.ProcessId == processId);
    }

    private sealed class FakeLauncher : ISupervisorProcessLauncher
    {
        public int Calls { get; private set; }
        public string? Error { get; set; }

        public string? EnsureRunning()
        {
            Calls++;
            return Error;
        }
    }

    private sealed class FakeForegroundWatcher : IForegroundWatcher
    {
        public ApexMapper.Core.ForegroundContext Current { get; set; } = ApexMapper.Core.ForegroundContext.Empty;

        public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged
        {
            add { }
            remove { }
        }

        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed record Harness(
        MappingSession Session,
        KeyStateStore Store,
        MappingEngine Engine,
        FakeChannel Channel,
        FakePreflightCheck Preflight,
        FakeProcessEnumerator Processes,
        FakeLauncher Launcher,
        FakeForegroundWatcher Foreground,
        List<string> ConfirmPrompts,
        List<MappingSessionStateChangedEventArgs> States);

    private static Harness Build(
        PreflightIssue? preflightResult = null,
        bool confirmAnswer = true,
        Func<string, string, bool>? confirm = null)
    {
        var store = new KeyStateStore(new KeyIndex(new[] { Throttle }));
        var engine = new MappingEngine(store, new NullSink());
        engine.SetEnabled(false); // composition starts the engine disabled
        var channel = new FakeChannel();
        var check = new FakePreflightCheck("test-check", preflightResult);
        var processes = new FakeProcessEnumerator();
        var launcher = new FakeLauncher();
        var foreground = new FakeForegroundWatcher();
        var prompts = new List<string>();
        var states = new List<MappingSessionStateChangedEventArgs>();

        var session = new MappingSession(
            store,
            engine,
            channel,
            new PreflightRunner(new IPreflightCheck[] { check }),
            new AntiCheatDetector(processes),
            new SteamDetector(processes, new[] { @"C:\SteamLibrary" }),
            launcher,
            foreground,
            confirm: confirm ?? ((_, message) =>
            {
                prompts.Add(message);
                return confirmAnswer;
            }),
            NullLogger<MappingSession>.Instance);
        session.StateChanged += (_, e) => states.Add(e);

        return new Harness(session, store, engine, channel, check, processes, launcher, foreground, prompts, states);
    }

    private static ApexMapper.Core.ForegroundContext GameContext(string exe = @"C:\Games\Forza.exe") =>
        new(exe, "Forza", 4242u, null, DateTimeOffset.UtcNow);

    // ---------------------------------------------------------------------------
    // Enable — happy path
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_with_clean_preflight_and_scan_turns_everything_on()
    {
        var h = Build();

        var enabled = await h.Session.EnableAsync(CancellationToken.None);

        enabled.Should().BeTrue();
        h.Session.IsEnabled.Should().BeTrue();
        h.Engine.IsEnabled.Should().BeTrue();
        h.Launcher.Calls.Should().Be(1);
        h.Channel.ConnectCalls.Should().Be(1);
        h.ConfirmPrompts.Should().BeEmpty("a clean scan needs no confirmation");
        h.States.Should().ContainSingle().Which.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Enable_when_already_enabled_short_circuits()
    {
        var h = Build();
        await h.Session.EnableAsync(CancellationToken.None);

        var again = await h.Session.EnableAsync(CancellationToken.None);

        again.Should().BeTrue();
        h.Preflight.Runs.Should().Be(1, "an already-enabled session must not re-run the flow");
        h.Channel.ConnectCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------------------
    // Enable — fail-closed branches
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Preflight_blocker_keeps_output_disabled_and_surfaces_remediation()
    {
        var h = Build(preflightResult: new PreflightIssue(
            "vigem-bus", PreflightSeverity.Fail, "ViGEmBus driver not found.", "Install ViGEmBus 1.22.0."));

        var enabled = await h.Session.EnableAsync(CancellationToken.None);

        enabled.Should().BeFalse();
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.Launcher.Calls.Should().Be(0, "fail-closed: nothing starts past a blocker");
        h.Channel.ConnectCalls.Should().Be(0);
        var state = h.States.Should().ContainSingle().Subject;
        state.IsEnabled.Should().BeFalse();
        state.Message.Should().Contain("ViGEmBus driver not found.").And.Contain("Install ViGEmBus 1.22.0.");
    }

    [Fact]
    public async Task Preflight_warn_does_not_block()
    {
        var h = Build(preflightResult: new PreflightIssue(
            "steam", PreflightSeverity.Warn, "advisory", null));

        (await h.Session.EnableAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task AntiCheat_positive_requires_confirmation_and_declining_stays_disabled()
    {
        var h = Build(confirmAnswer: false);
        h.Processes.Processes.Add(new ProcessSnapshot(7, 1, "BEService.exe", null, new Dictionary<string, string>()));

        var enabled = await h.Session.EnableAsync(CancellationToken.None);

        enabled.Should().BeFalse();
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.ConfirmPrompts.Should().ContainSingle().Which.Should().Contain("BEService.exe");
        h.Launcher.Calls.Should().Be(0);
        h.Channel.ConnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task AntiCheat_positive_with_confirmation_enables()
    {
        var h = Build(confirmAnswer: true);
        h.Processes.Processes.Add(new ProcessSnapshot(7, 1, "EasyAntiCheat.exe", null, new Dictionary<string, string>()));

        var enabled = await h.Session.EnableAsync(CancellationToken.None);

        enabled.Should().BeTrue();
        h.ConfirmPrompts.Should().ContainSingle("a positive scan must never enable silently");
        h.Session.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_antiCheat_scan_is_failClosed_and_requires_confirmation()
    {
        var h = Build(confirmAnswer: false);
        h.Processes.ThrowOnEnumerate = new InvalidOperationException("scan broken");

        var enabled = await h.Session.EnableAsync(CancellationToken.None);

        enabled.Should().BeFalse();
        h.ConfirmPrompts.Should().ContainSingle("an unattestable environment must not enable silently");
    }

    [Fact]
    public async Task Launcher_failure_keeps_output_disabled_with_the_error_surfaced()
    {
        var h = Build();
        h.Launcher.Error = "Supervisor executable not found at 'X'.";

        var enabled = await h.Session.EnableAsync(CancellationToken.None);

        enabled.Should().BeFalse();
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.Channel.ConnectCalls.Should().Be(0);
        h.States.Should().ContainSingle().Which.Message.Should().Contain("Supervisor executable not found");
    }

    [Fact]
    public async Task Steam_advisory_warns_but_still_enables()
    {
        var h = Build();
        h.Foreground.Current = GameContext(@"C:\SteamLibrary\Forza\Forza.exe");

        var enabled = await h.Session.EnableAsync(CancellationToken.None);

        enabled.Should().BeTrue();
        h.States.Should().ContainSingle().Which.Message.Should().Contain("Steam");
    }

    // ---------------------------------------------------------------------------
    // Disable
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Disable_turns_the_engine_off_gates_held_keys_and_disconnects()
    {
        var h = Build();
        await h.Session.EnableAsync(CancellationToken.None);
        h.Store.Set(Throttle, 1f, KeyProvenance.Digital);

        await h.Session.DisableAsync(CancellationToken.None);

        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.Store.Get(Throttle).Value.Should().Be(0f, "a held key must be zeroed at the disable transition");
        h.Store.IsGated(Throttle).Should().BeTrue("the held key must release once before it maps again");
        h.Channel.DisconnectCalls.Should().Be(1);
    }

    [Fact]
    public async Task Disable_completes_locally_even_when_the_channel_throws()
    {
        var h = Build();
        await h.Session.EnableAsync(CancellationToken.None);
        h.Store.Set(Throttle, 1f, KeyProvenance.Digital);
        h.Channel.ThrowOnDisconnect = new InvalidOperationException("wedged pipe");

        await h.Session.DisableAsync(CancellationToken.None);

        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.Store.Get(Throttle).Value.Should().Be(0f);
    }

    [Fact]
    public async Task Disable_does_local_off_before_disconnecting_the_channel()
    {
        // Ordering contract: the engine must be off and held keys gated BEFORE
        // the channel is asked to zero+disconnect, so a wedged channel can never
        // delay the local safety writes.
        var h = Build();
        await h.Session.EnableAsync(CancellationToken.None);
        h.Store.Set(Throttle, 1f, KeyProvenance.Digital);

        bool engineEnabledAtDisconnect = true;
        bool gatedAtDisconnect = false;
        h.Channel.OnDisconnectEntered = () =>
        {
            engineEnabledAtDisconnect = h.Engine.IsEnabled;
            gatedAtDisconnect = h.Store.IsGated(Throttle);
        };

        await h.Session.DisableAsync(CancellationToken.None);

        h.Channel.DisconnectCalls.Should().Be(1);
        engineEnabledAtDisconnect.Should().BeFalse("the engine must already be disabled when the channel disconnects");
        gatedAtDisconnect.Should().BeTrue("held keys must already be gated when the channel disconnects");
    }

    // ---------------------------------------------------------------------------
    // Supervisor connectivity surfacing
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Supervisor_disconnect_while_enabled_surfaces_a_reconnecting_state()
    {
        var h = Build();
        await h.Session.EnableAsync(CancellationToken.None);
        h.States.Clear();

        h.Channel.RaiseStatus(connected: false, error: "pipe broken");

        var state = h.States.Should().ContainSingle().Subject;
        state.IsEnabled.Should().BeTrue("the user's enable still stands; only connectivity dropped");
        state.Message.Should().Contain("reconnecting");
    }

    [Fact]
    public async Task Supervisor_reconnect_while_enabled_surfaces_a_connected_state()
    {
        var h = Build();
        await h.Session.EnableAsync(CancellationToken.None);
        h.Channel.RaiseStatus(connected: false, error: "pipe broken");
        h.States.Clear();

        h.Channel.RaiseStatus(connected: true, error: null);

        var state = h.States.Should().ContainSingle().Subject;
        state.IsEnabled.Should().BeTrue();
        state.Message.Should().Contain("connected");
    }

    [Fact]
    public void Supervisor_status_changes_while_disabled_are_not_surfaced()
    {
        var h = Build();

        h.Channel.RaiseStatus(connected: false, error: "pipe broken");
        h.Channel.RaiseStatus(connected: true, error: null);

        h.States.Should().BeEmpty("connectivity noise is irrelevant while mapping is off");
    }

    [Fact]
    public async Task Disable_when_already_disabled_is_a_noop()
    {
        var h = Build();

        await h.Session.DisableAsync(CancellationToken.None);

        h.Channel.DisconnectCalls.Should().Be(0);
        h.States.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------
    // ForceLocalOff (panic path)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ForceLocalOff_zeroes_and_gates_without_touching_the_channel()
    {
        var h = Build();
        await h.Session.EnableAsync(CancellationToken.None);
        h.Store.Set(Throttle, 1f, KeyProvenance.Digital);

        h.Session.ForceLocalOff("panic");

        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse();
        h.Store.Get(Throttle).Value.Should().Be(0f);
        h.Store.IsGated(Throttle).Should().BeTrue();
        h.Channel.DisconnectCalls.Should().Be(0, "the panic frame is the coordinator's job, not this method's");
        h.Channel.PanicCalls.Should().Be(0);
        h.States.Last().IsEnabled.Should().BeFalse();
        h.States.Last().Message.Should().Contain("panic");
    }

    [Fact]
    public void ForceLocalOff_never_throws_even_with_a_throwing_subscriber()
    {
        var h = Build();
        h.Session.StateChanged += (_, _) => throw new InvalidOperationException("bad subscriber");

        var act = () => h.Session.ForceLocalOff("panic");

        act.Should().NotThrow();
        h.Engine.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Panic_during_an_in_flight_enable_unwinds_the_enable_and_stays_off()
    {
        // An EnableAsync parked at the anti-cheat confirmation dialog (an
        // unbounded wait) can be outraced by a panic: without the panic-generation
        // guard the enable resumes after the panic, re-arms the engine, and leaves
        // live output post-panic with no explicit user enable.
        var confirmEntered = new ManualResetEventSlim(false);
        var releaseConfirm = new ManualResetEventSlim(false);

        var h = Build(confirm: (_, _) =>
        {
            confirmEntered.Set();
            releaseConfirm.Wait();
            return true; // user confirms — the enable would otherwise complete
        });
        // A positive anti-cheat verdict forces the confirmation prompt.
        h.Processes.Processes.Add(new ProcessSnapshot(7, 1, "BEService.exe", null, new Dictionary<string, string>()));
        h.Store.Set(Throttle, 1f, KeyProvenance.Digital);

        var enableTask = Task.Run(() => h.Session.EnableAsync(CancellationToken.None));
        confirmEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the enable must reach the confirm prompt");

        // Panic fires while the enable is still parked at the dialog.
        h.Session.ForceLocalOff("panic");

        // Let the enable resume; the generation guard must make it unwind.
        releaseConfirm.Set();
        var result = await enableTask;

        result.Should().BeFalse("a panic mid-enable must unwind the enable and report off");
        h.Session.IsEnabled.Should().BeFalse();
        h.Engine.IsEnabled.Should().BeFalse("the re-armed engine must be turned back off by the unwind");
        h.Store.Get(Throttle).Value.Should().Be(0f);
        h.Store.IsGated(Throttle).Should().BeTrue();
        h.Channel.IsConnected.Should().BeFalse("the unwind disconnects the channel it opened");
        h.States.Last().IsEnabled.Should().BeFalse();
    }
}
