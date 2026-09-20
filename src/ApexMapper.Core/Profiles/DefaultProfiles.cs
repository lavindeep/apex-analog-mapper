using ApexMapper.Core.Bindings;
using ApexMapper.Core.Keys;

namespace ApexMapper.Core.Profiles;

/// <summary>The profile that ships, and the reset point for custom ones.</summary>
public static class DefaultProfiles
{
    public const string ForzaId = "forza";

    /// <summary>
    /// Forza's Default Layout 1 controller mapping with WASD driving. Steering and
    /// throttle response defaults are provisional until the feel step in stage 5.
    /// </summary>
    public static Profile Forza()
    {
        var soft = Response.Response.Soft;
        var linear = Response.Response.Linear;
        return new Profile(
            ForzaId,
            "Forza",
            Keys:
            [
                new(Key.W, PadTarget.RightTrigger, soft, 0f, 0f),
                new(Key.S, PadTarget.LeftTrigger, soft, 0f, 0f),
                new(Key.Space, PadTarget.ButtonA, linear, 0f, 0f),
                new(Key.R, PadTarget.ButtonY, linear, 0f, 0f),
                new(Key.E, PadTarget.ButtonB, linear, 0f, 0f),
                new(Key.Q, PadTarget.ButtonX, linear, 0f, 0f),
                new(Key.LeftShift, PadTarget.LeftBumper, linear, 0f, 0f),
                new(Key.Tab, PadTarget.RightBumper, linear, 0f, 0f),
                new(Key.G, PadTarget.LeftStickClick, linear, 0f, 0f),
            ],
            Axes:
            [
                new(Key.A, Key.D, PadTarget.LeftStickX, soft, 0f, 0f, ConflictRule.LastInputWins, AxisMode.Position, AxisBinding.DefaultRateMs, AxisBinding.DefaultReturnMs),
                new(Key.Left, Key.Right, PadTarget.RightStickX, linear, 0f, 0f, ConflictRule.LastInputWins, AxisMode.Position, AxisBinding.DefaultRateMs, AxisBinding.DefaultReturnMs),
                new(Key.Down, Key.Up, PadTarget.RightStickY, linear, 0f, 0f, ConflictRule.LastInputWins, AxisMode.Position, AxisBinding.DefaultRateMs, AxisBinding.DefaultReturnMs),
            ]);
    }

    /// <summary>Scan codes used by the default profile, US layout.</summary>
    public static class Key
    {
        public static readonly ScanCode W = new(0x11);
        public static readonly ScanCode A = new(0x1E);
        public static readonly ScanCode S = new(0x1F);
        public static readonly ScanCode D = new(0x20);
        public static readonly ScanCode Q = new(0x10);
        public static readonly ScanCode E = new(0x12);
        public static readonly ScanCode R = new(0x13);
        public static readonly ScanCode G = new(0x22);
        public static readonly ScanCode Tab = new(0x0F);
        public static readonly ScanCode Space = new(0x39);
        public static readonly ScanCode LeftShift = new(0x2A);
        public static readonly ScanCode Up = new(0xE048);
        public static readonly ScanCode Down = new(0xE050);
        public static readonly ScanCode Left = new(0xE04B);
        public static readonly ScanCode Right = new(0xE04D);
    }
}
