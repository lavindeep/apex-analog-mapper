using System.Text.RegularExpressions;
using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.RawInput;

public static class RawInputDevicePath
{
    private static readonly Regex VidRegex = new(
        @"vid_([0-9a-f]{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PidRegex = new(
        @"pid_([0-9a-f]{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static DeviceIdentity Parse(string? path)
    {
        if (path is null)
        {
            return new DeviceIdentity(0, 0, null, null, null);
        }

        var vid = 0;
        var pid = 0;

        var vidMatch = VidRegex.Match(path);
        if (vidMatch.Success)
        {
            vid = int.Parse(
                vidMatch.Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var pidMatch = PidRegex.Match(path);
        if (pidMatch.Success)
        {
            pid = int.Parse(
                pidMatch.Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        return new DeviceIdentity(vid, pid, null, null, path);
    }
}
