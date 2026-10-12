using ApexMapper.Core.Profiles;
using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

/// <summary>
/// The evidence behind the tested list. Every Verified model has some: the maintainer's
/// TKL replies in tests/ApexMapper.Windows.Tests/fixtures, or a capture from the app's
/// sensor check in tests/ApexMapper.Core.Tests/Captures. Each shows the board answering
/// the sensor read on firmware verified for that model, and W, held during the check,
/// raising the sensor the built-in map gives W while no other sensor moves like a press.
/// docs/adding-a-keyboard.md says how to take a capture.
/// </summary>
public class TestedKeyboardEvidenceTests
{
    private const ushort MaintainersTkl = 0x1614;

    /// <summary>
    /// The TKL firmware the maintainer tested on the board itself, before captures existed.
    /// 4.9.1 is the firmware of the hex fixtures. 4.16.8 is what the board runs now.
    /// </summary>
    private static readonly (int ProductId, string Firmware)[] MaintainersFirmware = [(MaintainersTkl, "4.9.1"), (MaintainersTkl, "4.16.8")];

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".hex")).Trim();

    /// <summary>The maintainer's TKL readings in the same shape as a capture's replies.</summary>
    private static List<CapturedReply> TklReplies(string restPrefix = "rest", string heldPrefix = "w-held") =>
    [
        new("firmware", 0, Fixture("firmware")),
        .. Enumerable.Range(1, SensorRequest.GroupCount).Select(group => new CapturedReply("at rest", group, Fixture($"{restPrefix}-group{group}"))),
        .. Enumerable.Range(1, SensorRequest.GroupCount).Select(group => new CapturedReply("key held", group, Fixture($"{heldPrefix}-group{group}"))),
    ];

    private static IEnumerable<(string File, CaptureExport? Export, string? Error)> Captures()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "captures");
        foreach (var file in Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : [])
        {
            var export = CaptureExport.FromJson(File.ReadAllText(file), out var error);
            yield return (Path.GetFileName(file), export, error);
        }
    }

    [Fact]
    public void The_maintainers_tkl_readings_pass_and_readings_with_nothing_held_or_w_let_go_fail()
    {
        var problems = Problems(MaintainersTkl, "4.9.1", TklReplies());
        Assert.True(problems.Count == 0, string.Join(" ", problems));
        Assert.NotEmpty(Problems(MaintainersTkl, "4.9.1", TklReplies(heldPrefix: "rest")));
        Assert.NotEmpty(Problems(MaintainersTkl, "4.9.1", TklReplies(restPrefix: "w-held", heldPrefix: "rest")));
    }

    [Fact]
    public void Every_capture_shows_its_tested_model_answering_and_w_raising_its_mapped_sensor()
    {
        Assert.All(Captures(), capture =>
        {
            var problems = capture.Export is { } export
                ? Problems(export.ProductId, export.Firmware, export.Replies)
                : [capture.Error ?? "The file could not be read."];
            // Whole, since the collection's own message cuts each one short.
            Assert.True(problems.Count == 0, $"{capture.File}: {string.Join(" ", problems)}");
        });
    }

    [Fact]
    public void Every_tested_model_and_firmware_has_evidence()
    {
        var withEvidence = Captures()
            .Where(c => c.Export is not null)
            .Select(c => (c.Export!.ProductId, c.Export.Firmware))
            .Concat(MaintainersFirmware)
            .ToHashSet();

        Assert.All(KnownKeyboards.All.Where(m => m.Support == SensorSupport.Verified), model =>
        {
            Assert.True(model.VerifiedFirmware.Count > 0, $"{model.Name} ({model.ProductId:X4}) is Verified with no firmware.");
            var missing = model.VerifiedFirmware.Where(f => !withEvidence.Contains((model.ProductId, f))).ToList();
            Assert.True(missing.Count == 0, $"{model.Name} ({model.ProductId:X4}) firmware {string.Join(", ", missing)} is Verified without a capture.");
        });
    }

    /// <summary>What is wrong with one board's evidence, in words a contributor can act on. Empty when it holds up.</summary>
    private static List<string> Problems(int productId, string firmware, IReadOnlyList<CapturedReply> replies)
    {
        var problems = new List<string>();
        if (productId is < 0 or > ushort.MaxValue)
        {
            problems.Add($"Product {productId:X} is not a USB product ID.");
        }
        else if (!KnownKeyboards.IsVerified((ushort)productId, firmware))
        {
            problems.Add($"Product {productId:X4} with firmware {firmware} is not Verified in KnownKeyboards.");
        }
        var firmwareReplies = replies.Where(r => r.Label == "firmware").ToList();
        if (firmwareReplies is not [{ Group: 0 } reply]
            || SensorProtocol.ParseFirmware(Convert.FromHexString(reply.ReplyHex), out var version) is not null
            || version != firmware)
        {
            problems.Add($"The capture needs one firmware reply that reads {firmware}.");
        }
        var rest = Samples(replies, "at rest", problems);
        var held = Samples(replies, "key held", problems);
        if (problems.Count > 0)
        {
            return problems;
        }
        Assert.True(SensorMap.Default.TryGetSensorIndex(DefaultProfiles.Key.W, out var w));
        var rise = held[w] - rest[w];
        if (rise < LearnStep.MinimumDelta)
        {
            problems.Add($"Holding W should raise sensor {w} by at least {LearnStep.MinimumDelta} counts. It moved {rise}.");
        }
        for (var i = 0; i < SensorProtocol.SensorCount; i++)
        {
            if (i != w && Math.Abs(held[i] - rest[i]) >= LearnStep.MinimumDelta)
            {
                problems.Add($"Sensor {i} moved {held[i] - rest[i]} counts while W was held, as much as a press. Hold only W.");
            }
        }
        // The same margin Learn needs, so a close runner-up fails here too.
        var learn = new LearnStep(rest);
        learn.Observe(held);
        if (problems.Count == 0 && learn.Result() is var result && (result.Outcome != LearnOutcome.Found || result.SensorIndex != w))
        {
            problems.Add($"Learn would not pick sensor {w} for W. It found {result.Outcome}, with sensor {result.SecondIndex} moving {result.SecondDelta} counts against {result.Delta}.");
        }
        return problems;
    }

    /// <summary>The raw samples of groups 1 to 5, or a problem for each group that is missing, repeated or not sensor data.</summary>
    private static ushort[] Samples(IReadOnlyList<CapturedReply> replies, string label, List<string> problems)
    {
        var raw = new ushort[SensorProtocol.SensorCount];
        var labelled = replies.Where(r => r.Label == label).ToList();
        if (labelled.Any(r => r.Group is < 1 or > SensorRequest.GroupCount))
        {
            problems.Add($"A \"{label}\" reply names a group outside 1 to {SensorRequest.GroupCount}.");
        }
        for (var group = 1; group <= SensorRequest.GroupCount; group++)
        {
            if (labelled.Where(r => r.Group == group).ToList() is not [var reply])
            {
                problems.Add($"The capture needs one \"{label}\" reply for group {group}. The sensor check has to pass before the export.");
                continue;
            }
            var slice = raw.AsSpan((group - 1) * SensorProtocol.SensorsPerGroup, SensorProtocol.SensorsPerGroup);
            if (SensorProtocol.ParseGroup(Convert.FromHexString(reply.ReplyHex), slice, new ushort[SensorProtocol.SensorsPerGroup]) is { } error)
            {
                problems.Add($"\"{label}\" group {group}: {error}");
            }
            else if (!SensorProtocol.IsPlausibleGroup(slice))
            {
                problems.Add($"\"{label}\" group {group} is all one value, which is not sensor data.");
            }
        }
        return raw;
    }
}
