using Xunit;

namespace ApexMapper.Output.Tests;

/// <summary>Opts into assertions that require a Windows machine without ViGEmBus.</summary>
public sealed class DriverlessFactAttribute : FactAttribute
{
    public DriverlessFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows native ViGEm APIs.";
        else if (Environment.GetEnvironmentVariable("APEX_TEST_DRIVERLESS") != "1")
            Skip = "Requires ViGEmBus to be absent. Set APEX_TEST_DRIVERLESS=1 only on a driverless Windows test machine.";
    }
}
