namespace PanguBridge.Mapping;

/// <summary>
/// Default HIDMaestro button map and display names for the HIDMaestro tab's mapping table.
/// The default is a 1:1 identity mapping for standard buttons/D-pad, plus the extra-button
/// assignments documented in HidMaestroOutput.BuildReport's bit-level comments.
/// </summary>
public static class HmMapping
{
    /// <summary>HmSourceButton values in the same order as InputTestView's rows (minus the
    /// analog sticks/triggers, which aren't remappable - see AnalogAdjustment instead).</summary>
    public static readonly IReadOnlyList<HmSourceButton> SourceOrder = new[]
    {
        HmSourceButton.DPadUp, HmSourceButton.DPadDown, HmSourceButton.DPadLeft, HmSourceButton.DPadRight,
        HmSourceButton.A, HmSourceButton.B, HmSourceButton.X, HmSourceButton.Y,
        HmSourceButton.LeftShoulder, HmSourceButton.RightShoulder,
        HmSourceButton.LeftThumbClick, HmSourceButton.RightThumbClick,
        HmSourceButton.Start, HmSourceButton.Back,
        HmSourceButton.Ai, HmSourceButton.Fn, HmSourceButton.Profile,
        HmSourceButton.M1, HmSourceButton.M2, HmSourceButton.M3, HmSourceButton.M4,
        HmSourceButton.Lm, HmSourceButton.Rm,
    };

    /// <summary>All valid dropdown choices, in display order, "None (unused)" first.</summary>
    public static readonly IReadOnlyList<HmOutputButton> OutputOrder = new[]
    {
        HmOutputButton.None,
        HmOutputButton.A, HmOutputButton.B, HmOutputButton.X, HmOutputButton.Y,
        HmOutputButton.LeftShoulder, HmOutputButton.RightShoulder,
        HmOutputButton.LeftThumbClick, HmOutputButton.RightThumbClick,
        HmOutputButton.Start, HmOutputButton.Back,
        HmOutputButton.DPadUp, HmOutputButton.DPadDown, HmOutputButton.DPadLeft, HmOutputButton.DPadRight,
        HmOutputButton.Guide, HmOutputButton.Mute,
        HmOutputButton.LeftPaddle, HmOutputButton.RightPaddle,
        HmOutputButton.LeftFnButton, HmOutputButton.RightFnButton,
        HmOutputButton.TouchpadClick, HmOutputButton.TouchpadLeftTouch, HmOutputButton.TouchpadRightTouch,
    };

    public static Dictionary<HmSourceButton, HmOutputButton> CreateDefault() => new()
    {
        [HmSourceButton.DPadUp] = HmOutputButton.DPadUp,
        [HmSourceButton.DPadDown] = HmOutputButton.DPadDown,
        [HmSourceButton.DPadLeft] = HmOutputButton.DPadLeft,
        [HmSourceButton.DPadRight] = HmOutputButton.DPadRight,
        [HmSourceButton.A] = HmOutputButton.A,
        [HmSourceButton.B] = HmOutputButton.B,
        [HmSourceButton.X] = HmOutputButton.X,
        [HmSourceButton.Y] = HmOutputButton.Y,
        [HmSourceButton.LeftShoulder] = HmOutputButton.LeftShoulder,
        [HmSourceButton.RightShoulder] = HmOutputButton.RightShoulder,
        [HmSourceButton.LeftThumbClick] = HmOutputButton.LeftThumbClick,
        [HmSourceButton.RightThumbClick] = HmOutputButton.RightThumbClick,
        [HmSourceButton.Start] = HmOutputButton.Start,
        [HmSourceButton.Back] = HmOutputButton.Back,
        [HmSourceButton.Ai] = HmOutputButton.Guide,
        [HmSourceButton.Fn] = HmOutputButton.TouchpadClick,
        [HmSourceButton.Profile] = HmOutputButton.Mute,
        [HmSourceButton.M1] = HmOutputButton.RightFnButton,
        [HmSourceButton.M2] = HmOutputButton.RightPaddle,
        [HmSourceButton.M3] = HmOutputButton.LeftFnButton,
        [HmSourceButton.M4] = HmOutputButton.LeftPaddle,
        [HmSourceButton.Lm] = HmOutputButton.TouchpadLeftTouch,
        [HmSourceButton.Rm] = HmOutputButton.TouchpadRightTouch,
    };

    public static string DisplayName(HmSourceButton button) => button switch
    {
        HmSourceButton.DPadUp => "D-Pad Up",
        HmSourceButton.DPadDown => "D-Pad Down",
        HmSourceButton.DPadLeft => "D-Pad Left",
        HmSourceButton.DPadRight => "D-Pad Right",
        HmSourceButton.LeftShoulder => "Left Shoulder",
        HmSourceButton.RightShoulder => "Right Shoulder",
        HmSourceButton.LeftThumbClick => "Left Thumb Click",
        HmSourceButton.RightThumbClick => "Right Thumb Click",
        HmSourceButton.Ai => "AI",
        HmSourceButton.Fn => "FN",
        _ => button.ToString(),
    };

    // PS button name first, Xbox/XInput equivalent in parens - matches how this table's
    // options are actually displayed on a DualSense Edge, with the more familiar Xbox name
    // as a hint. Controls with no Xbox equivalent (touchpad, mute, paddles, Fn buttons) keep
    // a plain PS-style name with no parenthetical.
    public static string DisplayName(HmOutputButton button) => button switch
    {
        HmOutputButton.None => "None (unused)",
        HmOutputButton.A => "X (A)",
        HmOutputButton.B => "Circle (B)",
        HmOutputButton.X => "Square (X)",
        HmOutputButton.Y => "Triangle (Y)",
        HmOutputButton.LeftShoulder => "L1 (LB - Left Bumper)",
        HmOutputButton.RightShoulder => "R1 (RB - Right Bumper)",
        HmOutputButton.LeftThumbClick => "L3 (LS - Left Stick Click)",
        HmOutputButton.RightThumbClick => "R3 (RS - Right Stick Click)",
        HmOutputButton.Start => "Options (Start)",
        HmOutputButton.Back => "Create (Back)",
        HmOutputButton.DPadUp => "D-Pad Up",
        HmOutputButton.DPadDown => "D-Pad Down",
        HmOutputButton.DPadLeft => "D-Pad Left",
        HmOutputButton.DPadRight => "D-Pad Right",
        HmOutputButton.Guide => "PS (Guide - Home)",
        HmOutputButton.TouchpadClick => "Touchpad Click",
        HmOutputButton.Mute => "Mute Button",
        HmOutputButton.LeftPaddle => "Left Back Paddle",
        HmOutputButton.RightPaddle => "Right Back Paddle",
        HmOutputButton.LeftFnButton => "Left FN Button",
        HmOutputButton.RightFnButton => "Right FN Button",
        HmOutputButton.TouchpadLeftTouch => "Touchpad Left-Side Touch",
        HmOutputButton.TouchpadRightTouch => "Touchpad Right-Side Touch",
        _ => button.ToString(),
    };
}
