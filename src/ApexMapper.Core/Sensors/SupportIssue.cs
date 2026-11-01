namespace ApexMapper.Core.Sensors;

/// <summary>How the sensor check ended, as the keyboard support form asks it.</summary>
public enum SensorCheckResult
{
    NotRun,
    Passed,
    Failed,
}

/// <summary>
/// The link to a new keyboard support issue with every field the app knows filled in, so
/// a reporter only picks the connection and attaches the capture. Field ids and dropdown
/// labels match .github/ISSUE_TEMPLATE/keyboard_support.yml. GitHub leaves a dropdown
/// empty when the value matches no option, so a value with no matching option is left out.
/// </summary>
public static class SupportIssue
{
    public const string Form = "https://github.com/lavindeep/apex-analog-mapper/issues/new?template=keyboard_support.yml";

    /// <summary>The link for one board. A null leaves its field for the reporter.</summary>
    public static string Url(string? model, ushort? productId, string? firmware, SensorSupport? support, SensorCheckResult? check, string? appVersion)
    {
        var fields = new (string Id, string? Value)[]
        {
            ("model", model),
            ("hardware_id", productId is { } id ? $@"HID\VID_{KnownKeyboards.SteelSeriesVendorId:X4}&PID_{id:X4}" : null),
            ("firmware", firmware),
            ("card", support switch
            {
                SensorSupport.Unverified => "Not tested yet",
                SensorSupport.Unsupported => "Not supported",
                _ => null,
            }),
            ("check", check switch
            {
                SensorCheckResult.Passed => "It passed",
                SensorCheckResult.Failed => "It failed",
                SensorCheckResult.NotRun => "I didn't run it",
                _ => null,
            }),
            ("version", appVersion),
        };
        return Form + string.Concat(fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => $"&{f.Id}={Uri.EscapeDataString(f.Value!)}"));
    }
}
