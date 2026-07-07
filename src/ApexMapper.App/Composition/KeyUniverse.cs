using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Keys;

namespace ApexMapper.App.Composition;

/// <summary>
/// Builds the <see cref="KeyIndex"/> covering every scan code the raw-input
/// decoder can emit: the plain set-1 page plus the E0- and E1-prefixed pages
/// (0x01..0xFF each; 0x00 is not a valid base code). The mapping path requires
/// the index-backed <see cref="KeyStateStore"/> — the dictionary-backed default
/// is single-threaded only — and covering the full universe means no legitimate
/// key event is ever dropped for lack of a slot. 765 slots ≈ 6 KB of cells.
/// </summary>
internal static class KeyUniverse
{
    public static KeyIndex CreateFullIndex()
    {
        var prefixes = new byte[] { 0x00, 0xE0, 0xE1 };
        var keys = new List<KeyId>(prefixes.Length * 0xFF);
        foreach (var prefix in prefixes)
        {
            for (var code = 0x01; code <= 0xFF; code++)
            {
                keys.Add(ScanCodeEncoder.Encode(prefix, (byte)code));
            }
        }

        return new KeyIndex(keys);
    }
}
