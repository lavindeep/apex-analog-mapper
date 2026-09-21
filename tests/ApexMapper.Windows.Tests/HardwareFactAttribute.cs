using System.Runtime.CompilerServices;
using Xunit;

namespace ApexMapper.Windows.Tests;

/// <summary>A test that needs the maintainer's keyboard or an interactive desktop. Runs only with APEX_HW_TESTS=1.</summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public static bool Enabled => Environment.GetEnvironmentVariable("APEX_HW_TESTS") == "1";

    public HardwareFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!Enabled)
        {
            Skip = "Set APEX_HW_TESTS=1 to run hardware tests.";
        }
    }
}
