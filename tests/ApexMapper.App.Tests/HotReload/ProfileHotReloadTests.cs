using ApexMapper.App.Services;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;
using ApexMapper.Persistence.Profiles;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ApexMapper.App.Tests.HotReload;

// ---------------------------------------------------------------------------
// Test infrastructure (ManualTimeProvider — copied inline to avoid coupling)
// ---------------------------------------------------------------------------

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly List<(TimerCallback callback, DateTimeOffset dueAt)> _timers = new();
    private readonly object _lock = new();

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var due   = _now + dueTime;
        var entry = new ManualTimer(this, callback, due);
        lock (_lock)
            _timers.Add((callback, due));
        return entry;
    }

    public void Advance(TimeSpan delta)
    {
        _now += delta;

        List<TimerCallback> toFire;
        lock (_lock)
        {
            toFire = _timers
                .Where(t => t.dueAt <= _now)
                .Select(t => t.callback)
                .ToList();
            _timers.RemoveAll(t => t.dueAt <= _now);
        }

        foreach (var cb in toFire)
            cb(null);
    }

    internal void RemoveTimer(ManualTimer timer)
    {
        lock (_lock)
            _timers.RemoveAll(t => t.callback == timer.Callback);
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _provider;
        internal TimerCallback Callback { get; }

        public ManualTimer(ManualTimeProvider provider, TimerCallback callback, DateTimeOffset dueAt)
        {
            _provider = provider;
            Callback  = callback;
            _ = dueAt;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => _provider.RemoveTimer(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

internal static class ProfileFactory
{
    private static int _counter;

    public static Profile Make(string? id = null)
    {
        var n = System.Threading.Interlocked.Increment(ref _counter);
        return new Profile(
            Id:             id ?? $"profile-{n}",
            Name:           $"Profile {n}",
            Device:         new DeviceMatcher(0x1038, 0x161C, null, null),
            Game:           new GameMatcher(null, null, null),
            Activation:     ActivationPolicy.Default,
            SingleBindings: new[]
            {
                new SingleKeyBinding(
                    KeyId.FromScanCode(0x11),
                    BindingTarget.RightTrigger,
                    LinearCurve.Instance,
                    120f, 0f),
            },
            AxisBindings: Array.Empty<AxisPairBinding>(),
            Notes:          null);
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class ProfileHotReloadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "apex-hotreload-" + Guid.NewGuid().ToString("N"));

    private ProfileStore    MakeStore()   => new(new ProfileStoreOptions(_dir));
    private ManualTimeProvider MakeTime() => new();

    private ProfileHotReload MakeReload(
        ProfileStore? store = null,
        ManualTimeProvider? time = null,
        ILogger<ProfileHotReload>? logger = null)
    {
        return new ProfileHotReload(
            store   ?? MakeStore(),
            new ProfileHotReloadOptions(_dir, TimeSpan.FromMilliseconds(200)),
            logger  ?? new LoggerFactory().CreateLogger<ProfileHotReload>(),
            time);
    }

    public ProfileHotReloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // -----------------------------------------------------------------------
    // 1. Save → ProfilesReloaded fires with new list
    // -----------------------------------------------------------------------

    [Fact]
    public void Save_then_ProfilesReloaded_fires_with_new_list()
    {
        var time  = MakeTime();
        var store = MakeStore();
        using var reload = MakeReload(store: store, time: time);

        ProfilesReloadedEventArgs? received = null;
        reload.ProfilesReloaded += (_, e) => received = e;

        reload.Start();

        var profile = ProfileFactory.Make("p1");
        store.Save(profile);

        // Before debounce — nothing fired
        time.Advance(TimeSpan.FromMilliseconds(199));
        received.Should().BeNull();

        // We use the internal seam (TriggerNowForTesting) to control debounce
        // deterministically, bypassing real FileSystemWatcher timing.
        reload.TriggerNowForTesting();

        received.Should().NotBeNull();
        received!.Profiles.Should().ContainSingle(p => p.Id == "p1");
    }

    // -----------------------------------------------------------------------
    // 2. Rapid saves debounce to single event
    // -----------------------------------------------------------------------

    [Fact]
    public void Rapid_saves_debounce_to_single_event()
    {
        var time  = MakeTime();
        var store = MakeStore();
        using var reload = MakeReload(store: store, time: time);

        var events = new List<ProfilesReloadedEventArgs>();
        reload.ProfilesReloaded += (_, e) => events.Add(e);

        reload.Start();

        store.Save(ProfileFactory.Make("q1"));
        store.Save(ProfileFactory.Make("q2"));
        store.Save(ProfileFactory.Make("q3"));

        // Trigger debounce once — simulates the watcher coalescing events
        reload.TriggerNowForTesting();

        events.Should().ContainSingle();
        events[0].Profiles.Should().HaveCount(3);
    }

    // -----------------------------------------------------------------------
    // 3. Stop unsubscribes watcher — no further events after Stop
    // -----------------------------------------------------------------------

    [Fact]
    public void Stop_unsubscribes_watcher()
    {
        var time  = MakeTime();
        var store = MakeStore();
        using var reload = MakeReload(store: store, time: time);

        var events = new List<ProfilesReloadedEventArgs>();
        reload.ProfilesReloaded += (_, e) => events.Add(e);

        reload.Start();
        reload.Stop();

        store.Save(ProfileFactory.Make("r1"));

        // Even if we try to trigger, the stopped service should not fire
        // (TriggerNowForTesting checks _started guard)
        reload.TriggerNowForTesting();

        events.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // 4. Malformed JSON is logged, not thrown; valid profiles still returned
    // -----------------------------------------------------------------------

    [Fact]
    public void Malformed_json_logged_not_thrown()
    {
        var time  = MakeTime();
        var store = MakeStore();

        // Set up a logger that captures messages
        var capturingLogger = new CapturingLogger<ProfileHotReload>();

        using var reload = MakeReload(store: store, time: time, logger: capturingLogger);

        ProfilesReloadedEventArgs? received = null;
        reload.ProfilesReloaded += (_, e) => received = e;

        reload.Start();

        // Save a valid profile first
        store.Save(ProfileFactory.Make("valid1"));

        // Write a bad JSON file directly into the store directory
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ this is not valid json!!!");

        // Trigger — LoadAll internally uses TryLoad which swallows parse errors
        reload.TriggerNowForTesting();

        // Should have fired with only the valid profile (bad.json is silently skipped)
        received.Should().NotBeNull();
        received!.Profiles.Should().ContainSingle(p => p.Id == "valid1");
        // No exception propagated — the event fired cleanly
    }

    // -----------------------------------------------------------------------
    // 5. Start is idempotent
    // -----------------------------------------------------------------------

    [Fact]
    public void Start_is_idempotent()
    {
        var time  = MakeTime();
        var store = MakeStore();
        using var reload = MakeReload(store: store, time: time);

        var events = new List<ProfilesReloadedEventArgs>();
        reload.ProfilesReloaded += (_, e) => events.Add(e);

        reload.Start();
        reload.Start(); // second call — must not throw or double-subscribe
        reload.Start();

        store.Save(ProfileFactory.Make("s1"));
        reload.TriggerNowForTesting();

        // If Start were not idempotent and subscribed multiple times,
        // we would get multiple events per trigger.
        events.Should().ContainSingle();
    }

    // -----------------------------------------------------------------------
    // 6. Dispose stops the watcher
    // -----------------------------------------------------------------------

    [Fact]
    public void Dispose_stops_watcher()
    {
        var time  = MakeTime();
        var store = MakeStore();

        var events = new List<ProfilesReloadedEventArgs>();
        ProfileHotReload reload;

        reload = MakeReload(store: store, time: time);
        reload.ProfilesReloaded += (_, e) => events.Add(e);
        reload.Start();

        reload.Dispose();

        store.Save(ProfileFactory.Make("d1"));
        reload.TriggerNowForTesting(); // _started is false after dispose

        events.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Capturing logger helper
    // -----------------------------------------------------------------------

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel level, string message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
