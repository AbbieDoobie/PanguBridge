namespace PanguBridge.Controllers;

/// <summary>
/// Decodes the real DualSense Edge adaptive-trigger effect protocol from the raw 11-byte
/// blobs HIDMaestro hands back via HMOutputDecodedEventArgs.Fields["leftTriggerEffect"] /
/// ["rightTriggerEffect"] (bytes-passthrough - HIDMaestro does not interpret these itself).
/// Byte layout confirmed against Ohjurot/DualSense-Windows's DS5_Output.cpp (the de facto
/// reference implementation most PC games build against): byte 0 is a mode ID, the rest are
/// mode-specific parameters. Only the modes that library exposes are decoded - the real
/// hardware supports more (multi-zone "Feedback"/"Weapon" effects among them), but those are
/// rarer in the wild and would need their own guesswork to approximate anyway.
///
/// The Pangu has no physical trigger-resistance hardware, so ForceAt can only ever produce a
/// buzz-intensity approximation of what a real DualSense would make the trigger *feel* like at
/// a given pull position - never the resistance itself. That limitation is inherent to the
/// hardware, not a gap in this decoder.
/// </summary>
public enum TriggerEffectMode
{
    /// <summary>No resistance requested (mode 0x00), or a mode this decoder doesn't
    /// recognize (e.g. 0xFC Calibrate) - treated the same as off since simulating either
    /// would just be noise.</summary>
    Off,

    /// <summary>Mode 0x01 - uniform force from StartPosition to full pull.</summary>
    Continuous,

    /// <summary>Mode 0x02 - a binary "wall": full force between StartPosition and
    /// EndPosition, nothing outside that range.</summary>
    Section,

    /// <summary>Mode 0x26 - three-zone force curve (BeginForce/MiddleForce/EndForce)
    /// starting at StartPosition. The real hardware's exact interpolation between the three
    /// zones isn't publicly documented; ForceAt uses a straight-line blend, which is an
    /// approximation even before the Pangu's own hardware limits kick in.</summary>
    EffectEx,
}

public readonly struct TriggerEffectSample
{
    public TriggerEffectMode Mode { get; init; }
    public byte StartPosition { get; init; }
    public byte EndPosition { get; init; }   // Section only
    public byte Force { get; init; }         // Continuous only
    public byte BeginForce { get; init; }    // EffectEx
    public byte MiddleForce { get; init; }   // EffectEx
    public byte EndForce { get; init; }      // EffectEx

    public static readonly TriggerEffectSample Off = new() { Mode = TriggerEffectMode.Off };

    /// <summary>raw is the 11-byte blob exactly as HIDMaestro hands it back (mode byte at
    /// [0], parameters after) - matches DS5_Output.cpp's processTrigger buffer layout.</summary>
    public static TriggerEffectSample Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 11) return Off;

        return raw[0] switch
        {
            0x01 => new TriggerEffectSample
            {
                Mode = TriggerEffectMode.Continuous,
                StartPosition = raw[1],
                Force = raw[2],
            },
            0x02 => new TriggerEffectSample
            {
                Mode = TriggerEffectMode.Section,
                StartPosition = raw[1],
                EndPosition = raw[2],
            },
            // 0x02 | 0x20 | 0x04 per DS5_Output.cpp's EffectEx encoder.
            0x26 => new TriggerEffectSample
            {
                Mode = TriggerEffectMode.EffectEx,
                // The encoder stores 0xFF - startPosition; undo that here so StartPosition
                // means the same thing (a 0-255 pull position) across every mode.
                StartPosition = (byte)(0xFF - raw[1]),
                BeginForce = raw[4],
                MiddleForce = raw[5],
                EndForce = raw[6],
            },
            _ => Off,
        };
    }

    /// <summary>Approximate 0-255 force this effect implies at the given physical trigger
    /// pull position (0-255, same domain as GamepadState.LeftTrigger/RightTrigger). Called
    /// continuously as the trigger moves - not just when the effect itself changes - so the
    /// simulated buzz tracks the pull in real time the way real adaptive-trigger resistance
    /// would.</summary>
    public byte ForceAt(byte position)
    {
        switch (Mode)
        {
            case TriggerEffectMode.Continuous:
                return position >= StartPosition ? Force : (byte)0;

            case TriggerEffectMode.Section:
                return position >= StartPosition && position <= EndPosition ? (byte)255 : (byte)0;

            case TriggerEffectMode.EffectEx:
                if (position < StartPosition) return 0;
                int span = 255 - StartPosition;
                if (span <= 0) return MiddleForce;
                double t = (position - StartPosition) / (double)span;
                double force = t < 0.5
                    ? BeginForce + (MiddleForce - BeginForce) * (t / 0.5)
                    : MiddleForce + (EndForce - MiddleForce) * ((t - 0.5) / 0.5);
                return (byte)Math.Clamp(force, 0, 255);

            default:
                return 0;
        }
    }
}
