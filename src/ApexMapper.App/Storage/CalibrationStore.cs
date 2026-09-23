using System.IO;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;
using ApexMapper.Core.Storage;

namespace ApexMapper.App.Storage;

/// <summary>One keyboard's stored calibration.</summary>
/// <param name="Keys">Every key with a valid calibration, whatever firmware it was taken on.</param>
/// <param name="FromOtherFirmware">Keys calibrated on firmware other than the board's current one. They still load; the window warns and offers to calibrate them again.</param>
/// <param name="Signatures">Each group's signature recorded on the board's current firmware. A group recorded on other firmware, or never, is missing, and the poller checks it with its canary instead.</param>
/// <param name="Problem">What the window should tell the user about the file, or null.</param>
public sealed record KeyboardCalibration(
    IReadOnlyDictionary<ScanCode, KeyCalibration> Keys,
    IReadOnlySet<ScanCode> FromOtherFirmware,
    IReadOnlyDictionary<int, GroupSignature> Signatures,
    string? Problem);

/// <summary>
/// Calibration, one JSON file per keyboard in the calibration folder, named by the
/// board's container id. Each key, and each group's signature for the poller's desync
/// check, carries the firmware version it was recorded on, so after a firmware update
/// what was redone is no longer flagged and the rest is. A key is saved on its own as
/// soon as its calibration is complete. Saving refuses to write over a file that could
/// not be read or that a newer version wrote; a file of unreadable text is set aside
/// and the next save starts a new one. Called from the UI thread only.
/// </summary>
public sealed class CalibrationStore(string directory)
{
    public const int CurrentVersion = 1;

    private sealed record StoredKey(int Rest, int FullPress, int NoiseBand, int SensorIndex, string? Firmware);

    private sealed record StoredSignature(ushort AbsentMask, string? Firmware);

    private sealed record Document(Dictionary<ScanCode, StoredKey> Keys, Dictionary<int, StoredSignature>? Signatures = null);

    public KeyboardCalibration Load(Guid keyboard, string firmware)
    {
        var result = JsonFile.Load(PathOf(keyboard), Parse);
        var keys = new Dictionary<ScanCode, KeyCalibration>();
        var otherFirmware = new HashSet<ScanCode>();
        var invalid = 0;
        foreach (var (key, stored) in result.Value?.Keys ?? [])
        {
            if (stored is null || KeyCalibration.Validate(stored.Rest, stored.FullPress, stored.NoiseBand, stored.SensorIndex) is not null)
            {
                invalid++;
                continue;
            }
            keys[key] = new KeyCalibration(stored.Rest, stored.FullPress, stored.NoiseBand, stored.SensorIndex);
            if (!string.Equals(stored.Firmware, firmware, StringComparison.Ordinal))
            {
                otherFirmware.Add(key);
            }
        }
        var signatures = new Dictionary<int, GroupSignature>();
        foreach (var (group, stored) in result.Value?.Signatures ?? [])
        {
            if (stored is not null && group is >= 1 and <= SensorRequest.GroupCount && string.Equals(stored.Firmware, firmware, StringComparison.Ordinal))
            {
                signatures[group] = new GroupSignature(stored.AbsentMask);
            }
        }
        var problem = LoadProblem.Describe(result, "calibration");
        if (invalid > 0)
        {
            var dropped = invalid == 1 ? "One key's calibration was invalid" : $"{invalid} keys' calibrations were invalid";
            problem = $"{problem} {dropped} and must be calibrated again.".TrimStart();
        }
        return new KeyboardCalibration(keys, otherFirmware, signatures, problem);
    }

    /// <summary>Stores one key's calibration, taken on <paramref name="firmware"/>, alongside the board's other keys.</summary>
    public void Put(Guid keyboard, string firmware, ScanCode key, KeyCalibration calibration)
    {
        if (KeyCalibration.Validate(calibration.Rest, calibration.FullPress, calibration.NoiseBand, calibration.SensorIndex) is { } invalid)
        {
            throw new ArgumentException(invalid, nameof(calibration));
        }
        Update(keyboard, document =>
        {
            document.Keys[key] = new StoredKey(calibration.Rest, calibration.FullPress, calibration.NoiseBand, calibration.SensorIndex, firmware);
            return document;
        });
    }

    /// <summary>Stores a group's signature, taken with every key released on <paramref name="firmware"/>.</summary>
    public void PutSignature(Guid keyboard, string firmware, int group, GroupSignature signature)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(group, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(group, SensorRequest.GroupCount);
        Update(keyboard, document => document with
        {
            Signatures = new(document.Signatures ?? []) { [group] = new StoredSignature(signature.AbsentMask, firmware) },
        });
    }

    /// <summary>Read, change, write. Throws <see cref="IOException"/> rather than replace a file it could not read, which would drop the board's other keys.</summary>
    private void Update(Guid keyboard, Func<Document, Document> change)
    {
        var path = PathOf(keyboard);
        var result = JsonFile.Load(path, Parse);
        LoadProblem.ThrowIfUnsafeToSave(result, "calibration");
        var document = change(result.Value ?? new Document([]));
        JsonFile.Save(path, JsonDocuments.Serialize(CurrentVersion, document));
    }

    private string PathOf(Guid keyboard) => Path.Combine(directory, keyboard.ToString("D") + ".json");

    private static Parsed<Document> Parse(string text)
    {
        var parsed = JsonDocuments.Parse<Document>(text, CurrentVersion);
        return parsed.Value is { } document && document.Keys is null ? new(null, "The file has no keys.") : parsed;
    }
}
