using System.Runtime.CompilerServices;
using Xunit;

namespace ApexMapper.Windows.Tests;

/// <summary>The long soak against the real driver. Runs only with APEX_SOAK=1; APEX_SOAK_MINUTES changes its length.</summary>
public sealed class SoakFactAttribute : FactAttribute
{
    public const int DefaultMinutes = 10;

    public static bool Enabled => Environment.GetEnvironmentVariable("APEX_SOAK") == "1";

    public static int Minutes => int.TryParse(Environment.GetEnvironmentVariable("APEX_SOAK_MINUTES"), out var minutes) && minutes > 1 ? minutes : DefaultMinutes;

    public SoakFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Set APEX_SOAK=1 to run the soak.";
        SkipType = typeof(SoakFactAttribute);
        SkipUnless = nameof(Enabled);
    }
}
