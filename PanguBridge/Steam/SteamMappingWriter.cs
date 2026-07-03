using System.Text;
using HidSharp;
using PanguBridge.Mapping;

namespace PanguBridge.Steam;

/// <summary>
/// Builds the SDL mapping string for the current MappingProfile, for the user to paste
/// directly into Steam's own controller setup ("Paste from Clipboard") - Steam doesn't read
/// this from any config file; see docs/steam-integration.md.
/// </summary>
public static class SteamMappingWriter
{
    private const string ControllerName = "Pangu Bridge Virtual Controller";

    // SDL button name -> physical source button. Order matches docs/steam-integration.md.
    private static readonly (string SdlName, SourceButton Source)[] ButtonBindings =
    {
        ("a", SourceButton.A),
        ("b", SourceButton.B),
        ("x", SourceButton.X),
        ("y", SourceButton.Y),
        ("leftshoulder", SourceButton.LeftShoulder),
        ("rightshoulder", SourceButton.RightShoulder),
        ("start", SourceButton.Start),
        ("leftstick", SourceButton.LeftThumbClick),
        ("back", SourceButton.Back),
        ("rightstick", SourceButton.RightThumbClick),
        // AI maps to "guide" by choice, not because this hardware has a separate Guide
        // button (it doesn't - see SourceButton) - Steam shows "guide" with its own fixed
        // Steam-button-style icon, and AI is the closest thing this controller has to a
        // system/assistant button. FN maps to "misc1" by choice too - Steam's fixed
        // "Screenshot" label/icon for that slot fits FN's quick-capture-ish role.
        //
        // SDL's controller-mapping schema only defines misc1-misc6 - there is no misc7/8/9.
        // Steam's own clipboard round-trip silently drops misc7-9 entirely while keeping
        // misc1-6 intact. Steam also displays misc1-6 and the paddle1-4 slots with its own
        // fixed, uneditable names (e.g. "Screenshot" for misc1) - the mapping format has no
        // field for custom display text, only position. See docs/steam-integration.md for the
        // full extra-button-to-slot assignment and Steam's displayed names for each.
        ("guide", SourceButton.Ai),
        ("misc1", SourceButton.Fn),
        ("misc2", SourceButton.Lm),
        ("misc3", SourceButton.Rm),
        ("misc4", SourceButton.Profile),
        ("paddle1", SourceButton.M1),
        ("paddle2", SourceButton.M3),
        ("paddle3", SourceButton.M2),
        ("paddle4", SourceButton.M4),
    };

    // Axes and D-pad are fixed structurally (see VJoyOutput / docs/button-map.md) and are
    // not remapped by MappingProfile.
    private const string FixedAxisAndDpadBindings =
        "leftx:a0,lefty:a1,lefttrigger:a2,rightx:a3,righty:a4,righttrigger:a5," +
        "dpup:h0.1,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,";

    // vJoy's well-known default HID VendorID/ProductID, used as a fallback if the device
    // can't be found (e.g. not currently acquired). Confirmed identical across the
    // jshafer817/vJoy and BrunnerInnovation/vJoy forks via DEVPKEY_Device_HardwareIds
    // ("root\VID_1234&PID_BEAD&REV_0222").
    private const int VJoyFallbackVendorId = 0x1234;
    private const int VJoyFallbackProductId = 0xBEAD;

    public static string BuildGuid()
    {
        (int vendorId, int productId) = DetectVJoyVidPid();
        string vid = $"{vendorId & 0xFF:x2}{(vendorId >> 8) & 0xFF:x2}";
        string pid = $"{productId & 0xFF:x2}{(productId >> 8) & 0xFF:x2}";

        // SDL_JoystickGUID layout (16 bytes): bus(2)+pad(2), vendor(2)+pad(2),
        // product(2)+pad(2), version(2)+pad(2) - all little-endian. Steam/SDL compute this
        // from whatever device they actually see, which is vJoy's virtual HID joystick, not
        // the physical Beitong dongle (PanguBridge doesn't expose the physical device to
        // Steam at all). Version is left as 0000 since it isn't used for matching.
        return $"03000000{vid}0000{pid}000000000000";
    }

    /// <summary>
    /// Finds vJoy's actual HID VendorID/ProductID by enumerating connected HID devices for
    /// one whose product name contains "vJoy" - this is what Steam/SDL actually see and
    /// compute their GUID from. Dynamic detection (rather than hardcoding the fallback
    /// above) keeps this correct even if a different vJoy fork/version reports different IDs.
    /// </summary>
    private static (int VendorId, int ProductId) DetectVJoyVidPid()
    {
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            string? name;
            try
            {
                name = device.GetProductName();
            }
            catch (Exception)
            {
                continue;
            }

            if (name is not null && name.Contains("vJoy", StringComparison.OrdinalIgnoreCase))
                return (device.VendorID, device.ProductID);
        }

        return (VJoyFallbackVendorId, VJoyFallbackProductId);
    }

    public static string BuildMappingString(MappingProfile profile)
    {
        var sb = new StringBuilder();
        sb.Append(BuildGuid()).Append(',').Append(ControllerName).Append(",platform:Windows,");

        foreach (var (sdlName, source) in ButtonBindings)
        {
            if (profile.ButtonMap.TryGetValue(source, out int? slot) && slot is int vjoyButton)
            {
                sb.Append(sdlName).Append(":b").Append(vjoyButton - 1).Append(',');
            }
        }

        sb.Append(FixedAxisAndDpadBindings);
        return sb.ToString();
    }
}
