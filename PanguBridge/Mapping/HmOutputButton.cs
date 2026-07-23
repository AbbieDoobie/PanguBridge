namespace PanguBridge.Mapping;

/// <summary>
/// Every digital control the HIDMaestro DualSense Edge output can drive. An
/// <see cref="HmSourceButton"/> mapped to <see cref="None"/> is decoded but never sent.
/// Multiple source buttons may map to the same output - it fires if any of them are held.
/// See docs/button-map.md.
/// </summary>
public enum HmOutputButton
{
    None,
    A,
    B,
    X,
    Y,
    LeftShoulder,
    RightShoulder,
    LeftThumbClick,
    RightThumbClick,
    Start,
    Back,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    Guide,
    TouchpadClick,
    Mute,
    LeftPaddle,
    RightPaddle,
    LeftFnButton,
    RightFnButton,
    TouchpadLeftTouch,
    TouchpadRightTouch,
    TouchpadLeftClick,
    TouchpadRightClick,
}
