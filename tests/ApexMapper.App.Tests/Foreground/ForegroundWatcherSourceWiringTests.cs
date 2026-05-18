using ApexMapper.App.Services;
using ApexMapper.Core;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Foreground;

// ---------------------------------------------------------------------------
// Recording fake — tracks subscription/unsubscription
// ---------------------------------------------------------------------------

internal sealed class RecordingFakeWindowEventSource : IWindowEventSource
{
    public int  SubscribeCount   { get; private set; }
    public int  UnsubscribeCount { get; private set; }
    public bool Started          { get; private set; }
    public bool Stopped          { get; private set; }
    public bool Disposed         { get; private set; }

    private EventHandler<WindowFocusEvent>? _handler;

    public event EventHandler<WindowFocusEvent>? FocusChanged
    {
        add    { _handler += value; SubscribeCount++;   }
        remove { _handler -= value; UnsubscribeCount++; }
    }

    public void Start()   => Started  = true;
    public void Stop()    => Stopped  = true;
    public void Dispose() => Disposed = true;
}

/// <summary>Probe that always returns a fixed non-null context.</summary>
internal sealed class AlwaysResolvingProbe : IForegroundProbe
{
    private readonly ForegroundContext _ctx =
        new(@"C:\test.exe", "Test", 1u, null, DateTimeOffset.UtcNow);

    public ForegroundContext? Resolve(IntPtr hwnd, uint processId) => _ctx;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class ForegroundWatcherSourceWiringTests
{
    [Fact]
    public void Start_subscribes_to_FocusChanged_and_starts_source()
    {
        var src     = new RecordingFakeWindowEventSource();
        var watcher = new ForegroundWatcher(src, new AlwaysResolvingProbe());

        watcher.Start();

        src.SubscribeCount.Should().Be(1);
        src.Started.Should().BeTrue();
    }

    [Fact]
    public void Stop_unsubscribes_from_FocusChanged_and_stops_source()
    {
        var src     = new RecordingFakeWindowEventSource();
        var watcher = new ForegroundWatcher(src, new AlwaysResolvingProbe());

        watcher.Start();
        watcher.Stop();

        src.UnsubscribeCount.Should().Be(1);
        src.Stopped.Should().BeTrue();
    }

    [Fact]
    public void Dispose_unsubscribes_if_still_started()
    {
        var src     = new RecordingFakeWindowEventSource();
        var watcher = new ForegroundWatcher(src, new AlwaysResolvingProbe());

        watcher.Start();
        watcher.Dispose();

        src.UnsubscribeCount.Should().Be(1);
        src.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Start_is_idempotent()
    {
        var src     = new RecordingFakeWindowEventSource();
        var watcher = new ForegroundWatcher(src, new AlwaysResolvingProbe());

        watcher.Start();
        watcher.Start(); // second call should be no-op

        src.SubscribeCount.Should().Be(1);
    }

    [Fact]
    public void Stop_before_Start_is_safe()
    {
        var src     = new RecordingFakeWindowEventSource();
        var watcher = new ForegroundWatcher(src, new AlwaysResolvingProbe());

        // Should not throw or unsubscribe.
        watcher.Stop();

        src.UnsubscribeCount.Should().Be(0);
        src.Stopped.Should().BeFalse();
    }
}
