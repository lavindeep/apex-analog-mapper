namespace ApexMapper.App.Composition;

/// <summary>
/// Central source of truth for all file-system paths used by the App.
/// Inject this instead of hard-coding paths so tests and alternate
/// configurations can supply a different root.
/// </summary>
public interface IAppPaths
{
    /// <summary>%AppData%\ApexMapper\profiles — where profile JSON files are stored.</summary>
    string ProfilesDirectory { get; }

    /// <summary>%AppData%\ApexMapper\device-registry.json — device + calibration registry.</summary>
    string DeviceRegistryFile { get; }

    /// <summary>%AppData%\ApexMapper — directory used for panic-policy.json.</summary>
    string PanicPolicyDirectory { get; }

    /// <summary>%AppData%\ApexMapper — directory used for profile-pin.json.</summary>
    string ProfilePinDirectory { get; }

    /// <summary>%AppData%\ApexMapper\logs — application log output directory.</summary>
    string LogDirectory { get; }

    /// <summary>Full path to the current process's executable (for login-task registration).</summary>
    string ExecutablePath { get; }
}
