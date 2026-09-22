using System.IO;
using ApexMapper.Core.Calibration;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Storage;

namespace ApexMapper.App.Storage;

/// <summary>One keyboard's stored calibration.</summary>
/// <param name="Keys">Every key with a valid calibration, whatever firmware it was taken on.</param>
/// <param name="FromOtherFirmware">Keys calibrated on firmware other than the board's current one. They still load; the window warns and offers to calibrate them again.</param>
/// <param name="Problem">What the window should tell the user about the file, or null.</param>
public sealed record KeyboardCalibration(
    IReadOnlyDictionary<ScanCode, KeyCalibration> Keys,
    IReadOnlySet<ScanCode> FromOtherFirmware,
    string? Problem);

/// <summary>
/// Calibration, one JSON file per keyboard in the calibration folder, named by the
/// board's container id. Each key carries the firmware version it was calibrated on,
/// so after a firmware update the keys calibrated again are no longer flagged and the
/// rest are. A key is saved on its own as soon as its calibration is complete. Called
/// from the UI thread only.
/// </summary>
public sealed class CalibrationStore(string directory)
{
    public const int CurrentVersion = 1;

    private sealed record StoredKey(int Rest, int FullPress, int NoiseBand, int SensorIndex, string? Firmware);

    private sealed record Document(Dictionary<ScanCode, StoredKey> Keys);

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
        var problem = LoadProblem.Describe(result, "calibration");
        if (invalid > 0)
        {
            var dropped = invalid == 1 ? "One key's calibration was invalid" : $"{invalid} keys' calibrations were invalid";
            problem = $"{problem} {dropped} and must be calibrated again.".TrimStart();
        }
        return new KeyboardCalibration(keys, otherFirmware, problem);
    }

    /// <summary>
    /// Stores one key's calibration, taken on <paramref name="firmware"/>, alongside the
    /// board's other keys. Throws rather than overwrite a file that exists but cannot be read.
    /// </summary>
    public void Put(Guid keyboard, string firmware, ScanCode key, KeyCalibration calibration)
    {
        if (KeyCalibration.Validate(calibration.Rest, calibration.FullPress, calibration.NoiseBand, calibration.SensorIndex) is { } invalid)
        {
            throw new ArgumentException(invalid, nameof(calibration));
        }
        var path = PathOf(keyboard);
        var result = JsonFile.Load(path, Parse);
        if (result.Status == LoadStatus.Unavailable)
        {
            // Writing now would replace every other key's calibration with this one.
            throw new IOException($"The calibration file could not be opened: {result.Error}");
        }
        var keys = result.Value?.Keys ?? [];
        keys[key] = new StoredKey(calibration.Rest, calibration.FullPress, calibration.NoiseBand, calibration.SensorIndex, firmware);
        JsonFile.Save(path, JsonDocuments.Serialize(CurrentVersion, new Document(keys)));
    }

    private string PathOf(Guid keyboard) => Path.Combine(directory, keyboard.ToString("D") + ".json");

    private static (Document? Value, string? Error) Parse(string text)
    {
        var document = JsonDocuments.Deserialize<Document>(text, CurrentVersion, out var error);
        return document?.Keys is null ? (null, error ?? "The file has no keys.") : (document, null);
    }
}
