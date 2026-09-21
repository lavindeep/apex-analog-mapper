namespace ApexMapper.Core.Sensors;

/// <summary>
/// The only two things the app ever writes to the keyboard's vendor interface. The
/// constructor is private, so a request can only be built here: this type is the
/// command allowlist, and the vendor interface accepts nothing else.
/// </summary>
public readonly record struct SensorRequest
{
    public const byte FirmwareCommand = 0x90;
    public const byte GroupCommand = 0xD7;
    public const int GroupCount = 5;

    public byte Command { get; }
    public byte Selector { get; }

    private SensorRequest(byte command, byte selector)
    {
        Command = command;
        Selector = selector;
    }

    /// <summary>Firmware version query. The reply carries the version as ASCII at offset 1.</summary>
    public static SensorRequest Firmware() => new(FirmwareCommand, 0);

    /// <summary>Read one group of fourteen sensors, group 1..5.</summary>
    public static SensorRequest Group(int group)
    {
        if (group is < 1 or > GroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(group), group, "Sensor group must be 1..5.");
        }
        return new SensorRequest(GroupCommand, (byte)group);
    }

    public bool IsFirmware => Command == FirmwareCommand;

    /// <summary>A firmware query, or a group read for group 1..5. <c>default</c> is neither.</summary>
    public bool IsValid =>
        (Command == FirmwareCommand && Selector == 0)
        || (Command == GroupCommand && Selector is >= 1 and <= GroupCount);

    /// <summary>
    /// Fills a 65-byte output report: report id 0, command, selector, zeros. Refuses
    /// anything but the two allowed commands, so a <c>default</c> struct (a struct
    /// always has one) cannot reach the device.
    /// </summary>
    public void WriteTo(Span<byte> report)
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"Command 0x{Command:X2} selector {Selector} is not on the allowlist.");
        }
        if (report.Length != SensorProtocol.ReportLength)
        {
            throw new ArgumentException("Report buffer must be 65 bytes.", nameof(report));
        }
        report.Clear();
        report[1] = Command;
        report[2] = Selector;
    }
}
