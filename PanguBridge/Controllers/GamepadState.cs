namespace PanguBridge.Controllers;

/// <summary>
/// Full decoded state of a HID Interface 4 report (0x25 reports only). Every standard
/// button/axis/trigger/d-pad control is present in this same report alongside the 9
/// vendor-defined extra buttons (AI/FN/Profile/M1-M4/LM/RM) - see docs/hid-report-format.md
/// for the byte-level layout. No XInput reading is used anywhere in this app; see
/// docs/architecture.md.
/// </summary>
public readonly record struct GamepadState(
    bool A,
    bool B,
    bool X,
    bool Y,
    bool LeftShoulder,
    bool RightShoulder,
    bool LeftThumbClick,
    bool RightThumbClick,
    bool Start,
    bool Back,
    bool DPadUp,
    bool DPadDown,
    bool DPadLeft,
    bool DPadRight,
    bool Ai,
    bool Fn,
    bool Profile,
    bool M1,
    bool M2,
    bool M3,
    bool M4,
    bool Lm,
    bool Rm,
    byte LeftTrigger,
    byte RightTrigger,
    byte LeftStickX,
    byte LeftStickY,
    byte RightStickX,
    byte RightStickY)
{
    public static GamepadState Idle => default;

    // Sticks at center (128) so a pre-connect report doesn't max out as 32767.
    public static GamepadState Center => new(
        A: false, B: false, X: false, Y: false,
        LeftShoulder: false, RightShoulder: false,
        LeftThumbClick: false, RightThumbClick: false,
        Start: false, Back: false,
        DPadUp: false, DPadDown: false, DPadLeft: false, DPadRight: false,
        Ai: false, Fn: false, Profile: false,
        M1: false, M2: false, M3: false, M4: false,
        Lm: false, Rm: false,
        LeftTrigger: 0, RightTrigger: 0,
        LeftStickX: 128, LeftStickY: 128,
        RightStickX: 128, RightStickY: 128);

    public static GamepadState FromReport(byte[] r) => new(
        A: (r[9] & 0x10) != 0,
        B: (r[9] & 0x20) != 0,
        X: (r[9] & 0x40) != 0,
        Y: (r[9] & 0x80) != 0,
        LeftShoulder: (r[9] & 0x01) != 0,
        RightShoulder: (r[9] & 0x02) != 0,
        LeftThumbClick: (r[8] & 0x40) != 0,
        RightThumbClick: (r[8] & 0x80) != 0,
        Start: (r[8] & 0x10) != 0,
        Back: (r[8] & 0x20) != 0,
        DPadUp: (r[8] & 0x01) != 0,
        DPadDown: (r[8] & 0x02) != 0,
        DPadLeft: (r[8] & 0x04) != 0,
        DPadRight: (r[8] & 0x08) != 0,
        Ai: (r[10] & 0xC0) == 0xC0,
        Fn: (r[9] & 0x04) != 0,
        Profile: (r[9] & 0x08) != 0,
        M1: (r[10] & 0x01) != 0,
        M2: (r[10] & 0x02) != 0,
        M3: (r[10] & 0x04) != 0,
        M4: (r[10] & 0x08) != 0,
        Lm: (r[11] & 0x04) != 0,
        Rm: (r[11] & 0x08) != 0,
        LeftTrigger: r[16],
        RightTrigger: r[17],
        LeftStickX: r[2],
        LeftStickY: r[3],
        RightStickX: r[4],
        RightStickY: r[5]);
}
