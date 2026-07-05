namespace ApexMapper.Input.Abstractions.RawInput;

/// <summary>
/// Assigns small stable ids to raw-input device handles. Ids start at 1 and
/// are never reused for the map's lifetime, so a handle that reappears after
/// removal (including OS handle reuse for a different device) gets a fresh
/// id; 0 is reserved for "unknown device". Not thread-safe: owned by the
/// producing adapter's pump thread.
/// </summary>
public sealed class RawInputDeviceIdMap
{
    private readonly Dictionary<nint, int> _ids = new();
    private int _next;

    /// <summary>Returns the id for <paramref name="handle"/>, assigning the next id on first sight. A zero handle maps to 0.</summary>
    public int GetOrAdd(nint handle)
    {
        if (handle == 0) return 0;
        if (_ids.TryGetValue(handle, out var id)) return id;

        id = ++_next;
        _ids.Add(handle, id);
        return id;
    }

    /// <summary>Forgets <paramref name="handle"/> and returns the id it had, or 0 if it was never seen.</summary>
    public int Remove(nint handle)
    {
        return _ids.Remove(handle, out var id) ? id : 0;
    }
}
