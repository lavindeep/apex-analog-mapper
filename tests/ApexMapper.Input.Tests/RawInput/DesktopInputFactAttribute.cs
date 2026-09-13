namespace ApexMapper.Input.Tests.RawInput;

/// <summary>Requires explicit consent before tests inject keys into the active desktop.</summary>
public sealed class DesktopInputFactAttribute : FactAttribute
{
    public DesktopInputFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("APEX_TEST_DESKTOP_INPUT") != "1")
            Skip = "Injects real desktop keypresses. Set APEX_TEST_DESKTOP_INPUT=1 only on an idle test desktop.";
    }
}
