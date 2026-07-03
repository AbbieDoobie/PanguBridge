using PanguBridge.Controllers;

namespace PanguBridge.Mapping;

/// <summary>
/// Applies a physical button press/release to the vJoy slot assigned by the active
/// MappingProfile. A button with no entry (or a null slot) is decoded but never sent to vJoy.
/// </summary>
public sealed class ButtonMapper
{
    public MappingProfile Profile { get; set; }

    public ButtonMapper(MappingProfile profile)
    {
        Profile = profile;
    }

    public void Apply(SourceButton button, bool pressed, VJoyOutput output)
    {
        if (Profile.ButtonMap.TryGetValue(button, out int? vjoyButton) && vjoyButton is int slot)
        {
            output.SetButton(slot, pressed);
        }
    }
}
