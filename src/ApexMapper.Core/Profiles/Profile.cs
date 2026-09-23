using ApexMapper.Core.Bindings;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Sensors;

namespace ApexMapper.Core.Profiles;

/// <summary>
/// A named set of bindings. Each key and each pad target appears in at most one
/// binding. Record equality compares the lists by reference; compare serialised text
/// to detect an edit.
/// </summary>
public sealed record Profile(
    string Id,
    string Name,
    IReadOnlyList<KeyBinding> Keys,
    IReadOnlyList<AxisBinding> Axes)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name))
        {
            return "Profile needs an id and a name.";
        }
        if (Keys is null || Axes is null)
        {
            return "Profile is missing its bindings.";
        }
        var seen = new HashSet<ScanCode>();
        var targets = new HashSet<PadTarget>();
        foreach (var key in Keys)
        {
            if (key is null)
            {
                return "Profile has an empty key binding.";
            }
            if (key.Validate() is { } error)
            {
                return error;
            }
            if (!seen.Add(key.Key))
            {
                return $"{key.Key} is bound more than once.";
            }
            if (!targets.Add(key.Target))
            {
                return $"{key.Target} is bound more than once.";
            }
        }
        foreach (var axis in Axes)
        {
            if (axis is null)
            {
                return "Profile has an empty axis binding.";
            }
            if (axis.Validate() is { } error)
            {
                return error;
            }
            if (!seen.Add(axis.NegativeKey) || !seen.Add(axis.PositiveKey))
            {
                return "A key in an axis binding is bound more than once.";
            }
            if (!targets.Add(axis.Target))
            {
                return $"{axis.Target} is bound more than once.";
            }
        }
        return null;
    }

    /// <summary>Every key in any binding.</summary>
    public IEnumerable<ScanCode> AllKeys() =>
        Keys.Select(k => k.Key).Concat(Axes.SelectMany(a => new[] { a.NegativeKey, a.PositiveKey }));

    /// <summary>
    /// Keys the sensor drives: trigger and axis keys the map has a sensor for. Buttons
    /// stay digital even on a hall-effect key; nobody wants analog Space.
    /// </summary>
    public IEnumerable<ScanCode> AnalogKeys(SensorMap map) =>
        Keys.Where(k => k.Target.IsTrigger()).Select(k => k.Key)
            .Concat(Axes.SelectMany(a => new[] { a.NegativeKey, a.PositiveKey }))
            .Where(map.Supports)
            .Distinct();
}
