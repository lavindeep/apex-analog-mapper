using Nefarius.ViGEm.Client.Exceptions;

namespace ApexMapper.Output.ViGEm;

/// <summary>
/// Shared, human-readable descriptions of ViGEm client/driver failures.
///
/// Both the runtime output path (<see cref="ViGEmXboxOutput"/>) and the
/// pre-flight bus probe surface the same actionable wording — the descriptions
/// live here so the two never drift.
/// </summary>
internal static class ViGEmFailure
{
    public static string Describe(Exception ex) => ex switch
    {
        VigemBusNotFoundException =>
            "ViGEmBus driver not found. Install the ViGEmBus driver (1.22.0 or newer) from " +
            "https://github.com/nefarius/ViGEmBus/releases and reconnect.",
        VigemBusVersionMismatchException =>
            "The installed ViGEmBus driver is an incompatible version. Update ViGEmBus to 1.22.0 or newer.",
        VigemBusAccessFailedException =>
            "Access to the ViGEmBus driver was denied. Ensure ViGEmBus is installed correctly and try again.",
        VigemNoFreeSlotException =>
            "The ViGEmBus driver has no free controller slots. Disconnect other virtual controllers and retry.",
        DllNotFoundException =>
            "The ViGEm client native dependencies could not be loaded. The ViGEmBus driver must be " +
            "installed and the app must be running on Windows.",
        _ => $"The virtual Xbox controller could not be created: {ex.Message}",
    };
}
