using System;
using ApexMapper.App.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApexMapper.App.Tests.Services;

public sealed class ResumeGuardTests
{
    private sealed class FakePowerModeSource : IPowerModeSource
    {
        public int Disposed { get; private set; }

        public event EventHandler? Resumed;

        public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);

        public void Dispose() => Disposed++;
    }

    private sealed class RecordingSession : IMappingSession
    {
        public int ResumeCalls { get; private set; }

        public bool IsEnabled => false;

        public event EventHandler<MappingSessionStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public System.Threading.Tasks.Task<bool> EnableAsync(System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(false);

        public System.Threading.Tasks.Task DisableAsync(System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.CompletedTask;

        public void ForceLocalOff(string reason) { }

        public void OnSystemResumed() => ResumeCalls++;
    }

    [Fact]
    public void A_resume_after_Start_drives_the_session_gate()
    {
        var source = new FakePowerModeSource();
        var session = new RecordingSession();
        using var guard = new ResumeGuard(source, session, NullLogger<ResumeGuard>.Instance);
        guard.Start();

        source.RaiseResumed();

        session.ResumeCalls.Should().Be(1, "each resume must ask the session to gate held keys");
    }

    [Fact]
    public void A_resume_before_Start_is_ignored()
    {
        var source = new FakePowerModeSource();
        var session = new RecordingSession();
        using var guard = new ResumeGuard(source, session, NullLogger<ResumeGuard>.Instance);

        source.RaiseResumed();

        session.ResumeCalls.Should().Be(0, "the guard only reacts once started");
    }

    [Fact]
    public void Dispose_unsubscribes_so_a_later_resume_is_ignored()
    {
        var source = new FakePowerModeSource();
        var session = new RecordingSession();
        var guard = new ResumeGuard(source, session, NullLogger<ResumeGuard>.Instance);
        guard.Start();

        guard.Dispose();
        source.RaiseResumed();

        session.ResumeCalls.Should().Be(0, "a leaked handler on a static OS event would outlive the app");
    }

    [Fact]
    public void A_throwing_session_never_escapes_the_resume_handler()
    {
        var source = new FakePowerModeSource();
        var guard = new ResumeGuard(source, new ThrowingSession(), NullLogger<ResumeGuard>.Instance);
        guard.Start();

        var act = source.RaiseResumed;

        act.Should().NotThrow("a resume notification must never take the process down");
    }

    private sealed class ThrowingSession : IMappingSession
    {
        public bool IsEnabled => false;

        public event EventHandler<MappingSessionStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public System.Threading.Tasks.Task<bool> EnableAsync(System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(false);

        public System.Threading.Tasks.Task DisableAsync(System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.CompletedTask;

        public void ForceLocalOff(string reason) { }

        public void OnSystemResumed() => throw new InvalidOperationException("boom");
    }
}
