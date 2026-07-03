namespace PanguBridge.Mapping;

/// <summary>
/// Every physical button that can be remapped to a vJoy button slot. D-pad directions are
/// excluded - they drive the vJoy continuous POV hat directly and are not slot-remappable.
///
/// No "Guide" entry: the hardware has no separate Guide button. Pressing FN happened to also
/// set XInput's Guide bit when standard buttons were still read via XInputGetStateEx (see
/// docs/usb-reverse-engineering.md), but that was the same physical button as Fn below, not a
/// second control. Now that everything is read from HID Interface 4 directly, there's no
/// Guide bit at all in the decoded report (see docs/hid-report-format.md).
///
/// See docs/button-map.md.
/// </summary>
public enum SourceButton
{
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
