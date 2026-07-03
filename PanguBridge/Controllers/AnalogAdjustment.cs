namespace PanguBridge.Controllers;

/// <summary>
/// Shared byte-domain math for stick/trigger inversion and left/right swapping, used by both
/// PanguEngine's vJoy path and HidMaestroOutput's HIDMaestro path so the two backends always
/// agree on the result. Inversion is always applied to the physical stick/trigger first, and
/// swapping (if enabled) happens after - so e.g. "Invert Left Joystick Y-Axis" always affects
/// the physical left stick, regardless of whether Swap Left and Right Joysticks is also on.
/// </summary>
public static class AnalogAdjustment
{
    // The hardware's raw X byte runs high-to-low (high=left, low=right) - backwards from the
    // output convention both backends want - so X always needs this mandatory flip first;
    // "invert" then optionally flips it back on top of that correction. Y already matches the
    // output convention by default, so it only flips when "invert" is set.
    public static byte AdjustX(byte rawX, bool invert) => invert ? rawX : (byte)(255 - rawX);
    public static byte AdjustY(byte rawY, bool invert) => invert ? (byte)(255 - rawY) : rawY;

    public static (byte leftX, byte leftY, byte rightX, byte rightY) ComputeSticks(
        GamepadState s,
        bool invertLeftX, bool invertLeftY, bool invertRightX, bool invertRightY,
        bool swapSticks)
    {
        byte leftX  = AdjustX(s.LeftStickX,  invertLeftX);
        byte leftY  = AdjustY(s.LeftStickY,  invertLeftY);
        byte rightX = AdjustX(s.RightStickX, invertRightX);
        byte rightY = AdjustY(s.RightStickY, invertRightY);

        return swapSticks ? (rightX, rightY, leftX, leftY) : (leftX, leftY, rightX, rightY);
    }

    public static (byte left, byte right) ComputeTriggers(GamepadState s, bool swapTriggers) =>
        swapTriggers ? (s.RightTrigger, s.LeftTrigger) : (s.LeftTrigger, s.RightTrigger);
}
