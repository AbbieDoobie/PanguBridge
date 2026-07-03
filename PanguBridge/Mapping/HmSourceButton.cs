namespace PanguBridge.Mapping;

/// <summary>
/// Every digital (button-type) physical input, remappable to any <see cref="HmOutputButton"/>
/// via the HIDMaestro tab's mapping table. Sticks and triggers are analog and always drive
/// their own matching axis - see docs/button-map.md and AnalogAdjustment for the separate
/// swap/invert controls that apply to those instead. Order matches InputTestView's row order.
/// </summary>
public enum HmSourceButton
{
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
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
    Ai,
    Fn,
    Profile,
    M1,
    M2,
    M3,
    M4,
    Lm,
    Rm,
}
