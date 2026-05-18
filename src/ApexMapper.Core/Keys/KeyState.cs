namespace ApexMapper.Core.Keys;

public readonly record struct KeyState(float Value, KeyProvenance Source)
{
    public static KeyState Rest { get; } = new(0f, KeyProvenance.Digital);
}
