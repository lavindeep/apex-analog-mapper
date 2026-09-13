using ApexMapper.Core.Engine;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;

namespace ApexMapper.App.ViewModels.Profiles;

public sealed record BindingSummaryItem(string Input, string Output)
{
    public static IReadOnlyList<BindingSummaryItem> FromProfile(Profile profile) =>
        profile.SingleBindings
            .Select(binding => new BindingSummaryItem(KeyName(binding.Source), TargetName(binding.Target)))
            .Concat(profile.AxisBindings.Select(binding => new BindingSummaryItem(
                $"{KeyName(binding.NegativeKey)} / {KeyName(binding.PositiveKey)}", TargetName(binding.Target))))
            .ToArray();

    internal static string KeyName(KeyId key) => key.ScanCode switch
    {
        0x01 => "Esc",
        >= 0x02 and <= 0x0A => (key.ScanCode - 1).ToString(),
        0x0B => "0", 0x0C => "-", 0x0D => "=", 0x0E => "Backspace", 0x0F => "Tab",
        >= 0x10 and <= 0x19 => "QWERTYUIOP"[key.ScanCode - 0x10].ToString(),
        0x1A => "[", 0x1B => "]", 0x1C => "Enter", 0x1D => "Left Ctrl",
        >= 0x1E and <= 0x26 => "ASDFGHJKL"[key.ScanCode - 0x1E].ToString(),
        0x27 => ";", 0x28 => "'", 0x29 => "`", 0x2A => "Left Shift", 0x2B => "\\",
        >= 0x2C and <= 0x32 => "ZXCVBNM"[key.ScanCode - 0x2C].ToString(),
        0x33 => ",", 0x34 => ".", 0x35 => "/", 0x36 => "Right Shift",
        0x37 => "Numpad *", 0x38 => "Left Alt", 0x39 => "Space", 0x3A => "Caps Lock",
        >= 0x3B and <= 0x44 => $"F{key.ScanCode - 0x3A}",
        0x45 => "Num Lock", 0x46 => "Scroll Lock",
        0x47 => "Numpad 7", 0x48 => "Numpad 8", 0x49 => "Numpad 9", 0x4A => "Numpad -",
        0x4B => "Numpad 4", 0x4C => "Numpad 5", 0x4D => "Numpad 6", 0x4E => "Numpad +",
        0x4F => "Numpad 1", 0x50 => "Numpad 2", 0x51 => "Numpad 3",
        0x52 => "Numpad 0", 0x53 => "Numpad .", 0x57 => "F11", 0x58 => "F12",
        0xE01C => "Numpad Enter", 0xE01D => "Right Ctrl", 0xE035 => "Numpad /",
        0xE037 => "Print Screen", 0xE038 => "Right Alt", 0xE047 => "Home",
        0xE048 => "Up", 0xE049 => "Page Up", 0xE04B => "Left", 0xE04D => "Right",
        0xE04F => "End", 0xE050 => "Down", 0xE051 => "Page Down",
        0xE052 => "Insert", 0xE053 => "Delete", 0xE05B => "Left Windows",
        0xE05C => "Right Windows", 0xE05D => "Menu", 0xE11D => "Pause",
        _ => $"Scan code {key}",
    };

    internal static string TargetName(BindingTarget target) => target switch
    {
        BindingTarget.LeftStickX => "Left stick X",
        BindingTarget.LeftStickY => "Left stick Y",
        BindingTarget.RightStickX => "Right stick X",
        BindingTarget.RightStickY => "Right stick Y",
        BindingTarget.LeftTrigger => "Left trigger",
        BindingTarget.RightTrigger => "Right trigger",
        BindingTarget.ButtonA => "A button",
        BindingTarget.ButtonB => "B button",
        BindingTarget.ButtonX => "X button",
        BindingTarget.ButtonY => "Y button",
        BindingTarget.ButtonLB => "Left bumper",
        BindingTarget.ButtonRB => "Right bumper",
        BindingTarget.ButtonStart => "Start button",
        BindingTarget.ButtonBack => "Back button",
        BindingTarget.ButtonLS => "Left stick click",
        BindingTarget.ButtonRS => "Right stick click",
        BindingTarget.ButtonGuide => "Guide button",
        BindingTarget.DpadUp => "D-pad up",
        BindingTarget.DpadDown => "D-pad down",
        BindingTarget.DpadLeft => "D-pad left",
        BindingTarget.DpadRight => "D-pad right",
        _ => target.ToString(),
    };
}
