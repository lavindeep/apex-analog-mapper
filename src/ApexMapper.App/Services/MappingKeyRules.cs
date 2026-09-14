using ApexMapper.Core.Keys;

namespace ApexMapper.App.Services;

internal static class MappingKeyRules
{
    public const string ReservedKeyError = "Ctrl, Alt, Windows and F12 cannot be mapped. Choose another key.";

    public static bool IsReserved(KeyId key) => key.ScanCode is
        0x1D or 0xE01D or 0x38 or 0xE038 or 0xE05B or 0xE05C or 0x58;
}
