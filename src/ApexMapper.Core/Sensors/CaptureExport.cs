using ApexMapper.Core.Storage;

namespace ApexMapper.Core.Sensors;

/// <summary>One reply, as hex, with what the user was doing when it was taken.</summary>
public sealed record CapturedReply(string Label, int Group, string ReplyHex);

/// <summary>
/// What a user of an unverified keyboard attaches to a GitHub issue: everything needed
/// to add the board to the verified list without owning one.
/// </summary>
public sealed record CaptureExport(
    string AppVersion,
    DateTimeOffset TakenAt,
    int ProductId,
    string ProductName,
    string Firmware,
    string ContainerId,
    int InputReportLength,
    int OutputReportLength,
    IReadOnlyList<CapturedReply> Replies)
{
    public const int CurrentVersion = 1;

    public string ToJson() => JsonDocuments.Serialize(CurrentVersion, this);

    public static CaptureExport? FromJson(string text, out string? error) =>
        JsonDocuments.Deserialize<CaptureExport>(text, CurrentVersion, out error);
}
