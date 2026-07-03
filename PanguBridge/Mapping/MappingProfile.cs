namespace PanguBridge.Mapping;

/// <summary>
/// Maps each physical button to a vJoy button slot (1-19), or null to leave it unmapped
/// (decoded but not sent to vJoy). Pure in-memory model - persistence lives in
/// AppSettings.VJoyButtonMap, not in a separate profile file.
/// </summary>
public sealed class MappingProfile
{
    public string Name { get; set; } = "default";

    public Dictionary<SourceButton, int?> ButtonMap { get; set; } = new();

    /// <summary>1:1 default mapping matching the vJoy Button column in docs/button-map.md.</summary>
    public static MappingProfile CreateDefault() => new()
    {
        Name = "default",
        ButtonMap = new Dictionary<SourceButton, int?>
        {
            [SourceButton.A] = 1,
            [SourceButton.B] = 2,
            [SourceButton.X] = 3,
            [SourceButton.Y] = 4,
            [SourceButton.LeftShoulder] = 5,
            [SourceButton.RightShoulder] = 6,
            [SourceButton.LeftThumbClick] = 7,
            [SourceButton.RightThumbClick] = 8,
            [SourceButton.Start] = 9,
            [SourceButton.Back] = 10,
            [SourceButton.Ai] = 11,
            [SourceButton.Fn] = 12,
            [SourceButton.Profile] = 13,
            [SourceButton.M1] = 14,
            [SourceButton.M2] = 15,
            [SourceButton.M3] = 16,
            [SourceButton.M4] = 17,
            [SourceButton.Lm] = 18,
            [SourceButton.Rm] = 19,
        },
    };
}
