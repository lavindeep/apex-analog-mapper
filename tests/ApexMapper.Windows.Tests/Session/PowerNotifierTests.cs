using ApexMapper.Windows.Session;
using Xunit;

namespace ApexMapper.Windows.Tests.Session;

[Collection(ProcessSingletons.Name)]
public class PowerNotifierTests
{
    [Fact]
    public void Registration_succeeds_handlers_run_and_a_throwing_one_is_counted()
    {
        using var notifier = new PowerNotifier();
        var raised = 0;
        notifier.SleepOrWake += () => raised++;
        notifier.SleepOrWake += () => throw new InvalidOperationException("handler bug");

        notifier.Raise();

        Assert.Equal(1, raised);
        Assert.Equal(1, notifier.HandlerFaults);
    }

    [Fact]
    public void One_notifier_per_process_and_a_disposed_one_frees_the_slot()
    {
        var first = new PowerNotifier();
        Assert.Throws<InvalidOperationException>(() => new PowerNotifier());

        first.Dispose();

        using var second = new PowerNotifier();
    }
}
