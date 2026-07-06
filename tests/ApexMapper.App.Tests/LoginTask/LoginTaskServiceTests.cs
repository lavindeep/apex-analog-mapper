using ApexMapper.App.Services;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.LoginTask;

// ---------------------------------------------------------------------------
// Test double
// ---------------------------------------------------------------------------

internal sealed class FakeTaskSchedulerFacade : ITaskSchedulerFacade
{
    private readonly HashSet<string> _tasks = new(StringComparer.Ordinal);

    // Records of calls for assertion
    public List<(string taskName, string executablePath, string description)> RegisterCalls { get; } = new();
    public List<string> UnregisterCalls { get; } = new();

    public bool TaskExists(string taskName) => _tasks.Contains(taskName);

    public void RegisterLogonTask(string taskName, string executablePath, string description)
    {
        _tasks.Add(taskName);
        RegisterCalls.Add((taskName, executablePath, description));
    }

    public void UnregisterTask(string taskName)
    {
        _tasks.Remove(taskName);
        UnregisterCalls.Add(taskName);
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class LoginTaskServiceTests
{
    private const string DefaultTaskName = "ApexProAnalogMapper";
    private const string ExePath         = @"C:\Program Files\ApexMapper\ApexMapper.exe";
    private const string Desc            = "Apex Pro Analog Mapper login task";

    private static LoginTaskOptions Opts(string? name = null) =>
        new(ExePath, name ?? DefaultTaskName, Desc);

    // -----------------------------------------------------------------------
    // 1. IsEnabled returns false when no task registered
    // -----------------------------------------------------------------------

    [Fact]
    public void IsEnabled_returns_false_when_no_task()
    {
        var fake    = new FakeTaskSchedulerFacade();
        var service = new LoginTaskService(fake, Opts());

        service.IsEnabled().Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // 2. Enable registers the logon task
    // -----------------------------------------------------------------------

    [Fact]
    public void Enable_registers_logon_task()
    {
        var fake    = new FakeTaskSchedulerFacade();
        var service = new LoginTaskService(fake, Opts());

        service.Enable();

        fake.TaskExists(DefaultTaskName).Should().BeTrue();
        fake.RegisterCalls.Should().ContainSingle();
        fake.RegisterCalls[0].taskName.Should().Be(DefaultTaskName);
        fake.RegisterCalls[0].executablePath.Should().Be(ExePath);
    }

    // -----------------------------------------------------------------------
    // 3. Disable unregisters the task
    // -----------------------------------------------------------------------

    [Fact]
    public void Disable_unregisters_task()
    {
        var fake    = new FakeTaskSchedulerFacade();
        var service = new LoginTaskService(fake, Opts());

        service.Enable();
        service.Disable();

        fake.TaskExists(DefaultTaskName).Should().BeFalse();
        fake.UnregisterCalls.Should().Contain(DefaultTaskName);
    }

    // -----------------------------------------------------------------------
    // 4. Enable is idempotent — re-registers cleanly
    // -----------------------------------------------------------------------

    [Fact]
    public void Enable_is_idempotent()
    {
        var fake    = new FakeTaskSchedulerFacade();
        var service = new LoginTaskService(fake, Opts());

        service.Enable();
        service.Enable(); // second call — must not throw

        // Second Enable should remove then re-add, so 2 register calls total
        fake.RegisterCalls.Should().HaveCount(2);
        fake.TaskExists(DefaultTaskName).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // 5. Disable when not registered is a no-op
    // -----------------------------------------------------------------------

    [Fact]
    public void Disable_when_not_registered_is_noop()
    {
        var fake    = new FakeTaskSchedulerFacade();
        var service = new LoginTaskService(fake, Opts());

        var act = () => service.Disable();

        act.Should().NotThrow();
        fake.UnregisterCalls.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // 6. All three operations use the injected options' task name
    // -----------------------------------------------------------------------

    [Fact]
    public void Operations_use_injected_options_task_name_and_path()
    {
        const string customPath = @"D:\Tools\ApexMapper.exe";
        const string customName = "MyCustomTask";
        var opts    = new LoginTaskOptions(customPath, customName, "Custom description");
        var fake    = new FakeTaskSchedulerFacade();
        var service = new LoginTaskService(fake, opts);

        service.Enable();

        fake.RegisterCalls.Should().ContainSingle();
        var call = fake.RegisterCalls[0];
        call.taskName.Should().Be(customName);
        call.executablePath.Should().Be(customPath);
        call.description.Should().Be("Custom description");

        // IsEnabled / Disable must key off the SAME injected name, not a hardcoded one.
        service.IsEnabled().Should().BeTrue();
        service.Disable();
        service.IsEnabled().Should().BeFalse();
        fake.UnregisterCalls.Should().ContainSingle().Which.Should().Be(customName);
    }
}
