namespace ApexMapper.Core.Keys;

public readonly record struct KeyId(ushort ScanCode)
{
    public static KeyId FromScanCode(ushort scanCode) => new(scanCode);
    public override string ToString() => $"0x{ScanCode:X2}";
}
