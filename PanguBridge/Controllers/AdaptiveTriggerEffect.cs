namespace PanguBridge.Controllers;

/// <summary>
/// Decodes the DualSense adaptive-trigger effect protocol from the raw 11-byte blobs HIDMaestro
/// hands back via HMOutputDecodedEventArgs.Fields["leftTriggerEffect"] / ["rightTriggerEffect"]
/// (bytes-passthrough - HIDMaestro does not interpret these itself). Byte 0 is a mode ID, the
/// rest are mode-specific parameters.
///
/// Mode IDs and every parameter layout below are confirmed against
/// Nielk1/duaLib's triggerFactory.cpp/.h (github.com/WujekFoliarz/duaLib, MIT licensed,
/// explicitly revision-numbered and parameter-validated) - the same source PadForge's own
/// DualSense encoder and dualsensectl both independently trace back to. Its enum groups modes
/// into "officially recognized" (Off/Feedback/Weapon/Vibration - documented, expected to keep
/// working) and "unofficial but unique effects left in the firmware" (Bow/Galloping/Machine -
/// undocumented, discovered by watching real games' HID traffic; Galloping and Machine were
/// specifically reverse-engineered from Metro Exodus's own output). Every mode ID and byte
/// offset here was cross-checked against at least two independent sources; live capture against
/// a real game (Steam Input off) additionally confirmed Off/Bow/Weapon/Vibration byte-for-byte.
///
/// Off/Feedback/Weapon/Bow are purely position-driven - ForceAt only ever depends on where the
/// physical trigger currently sits, never on time. Vibration/Machine/Galloping are genuinely
/// time-varying on real hardware (each carries a frequency parameter the earlier decoder threw
/// away) - see IsSelfOscillating and each mode's *ForceAt below for how that's approximated.
/// </summary>
public readonly struct TriggerEffectSample
{
    private enum Kind : byte { Off, Static, Vibration, Machine, Galloping }

    private const int ZoneCount = 10;

    // 11-byte wire copy exactly as decoded, used only to tell whether a newly received effect
    // is genuinely different from the last one - see IsSameEffectAs. Games/Steam routinely
    // resend an unchanged effect on every rumble refresh (confirmed via live capture), and
    // treating each resend as "new" would restart Vibration/Machine/Galloping's oscillation
    // phase on every resend instead of letting it run continuously - see PanguEngine's use of
    // this for the per-side effect-start timestamp it feeds into ForceAt's elapsedSeconds.
    private readonly byte[] _raw;

    private readonly Kind _kind;

    // Static (Off/Feedback/Weapon/Bow): force per zone, time-independent. Vibration: peak
    // amplitude per zone that the sine term below modulates. Unused by Machine/Galloping, which
    // use a start/end range instead of a full per-zone array (their real params only carry a
    // range, not independently addressable zones).
    private readonly byte[]? _zoneForce;

    // Machine/Galloping: the effect is only active while the physical trigger's zone falls
    // within [StartZone, EndZone] - same range concept as Weapon/Bow, just not baked into a
    // zone-force array since these two also need the range for their own time math.
    private readonly byte _startZone;
    private readonly byte _endZone;

    // Machine only: the two force levels it alternates between (already scaled to 0-255).
    private readonly byte _amplitudeA;
    private readonly byte _amplitudeB;

    // Vibration/Machine/Galloping: cycle rate in Hz, straight from the wire byte - Nielk1's own
    // doc comments state this in hertz for all three modes.
    private readonly byte _frequencyHz;

    // Machine only: alternation period between _amplitudeA and _amplitudeB, in tenths of a
    // second per Nielk1's doc comment on TriggerEffectGenerator.Machine's period parameter.
    private readonly byte _periodTenthsSec;

    // Galloping only: phase (0-7 slots within one cycle) at which each of the two beats fires -
    // not a strength; Galloping carries no amplitude parameter at all (see GallopingForceAt).
    private readonly byte _firstFootPhase;
    private readonly byte _secondFootPhase;

    public static readonly TriggerEffectSample Off = new(new byte[11], Kind.Off);

    public bool IsOff => _kind == Kind.Off;

    /// <summary>True for Vibration/Machine/Galloping - modes that already vary over time on
    /// their own (buzzing/alternating/pulsing for as long as they're active), as opposed to
    /// Off/Feedback/Weapon/Bow which only ever change when the physical trigger's position
    /// does. PanguEngine.AdaptiveTriggerReleaseMs only applies release smoothing to the latter
    /// group - a fast single-shot pull through a static Weapon wall has no follow-up to fall
    /// back on if the buzz is missed, but Vibration/Machine/Galloping keep producing pulses of
    /// their own for as long as they're held, so smoothing has nothing useful to add and would
    /// mostly just blur the oscillation itself (their real-world frequencies - 15-33Hz in every
    /// captured/referenced example - land well inside typical release-time windows).</summary>
    public bool IsSelfOscillating => _kind is Kind.Vibration or Kind.Machine or Kind.Galloping;

    /// <summary>True when raw carries the exact same effect as this instance - used to decide
    /// whether an incoming HIDMaestro notification is a genuine change or just a routine resend
    /// of an unchanged effect (see the _raw field comment).</summary>
    public bool IsSameEffectAs(TriggerEffectSample other) => _raw.AsSpan().SequenceEqual(other._raw);

    /// <summary>True when the physical trigger's swept position range touches this effect's
    /// active zone(s) at all - position-only, ignoring whatever a self-oscillating effect's
    /// current force happens to be at this exact instant. Exists for
    /// AppSettings.AdaptiveTriggerDisableMatchingGrip: gating that suppression on ForceAt's
    /// instantaneous result (as before) meant Vibration/Machine's sine dipping to zero
    /// (working as designed, several times a second) briefly let a side's grip motor back on
    /// mid-buzz - and per that setting's own doc comment, grip and trigger sharing a side can't
    /// reliably sustain together, so that brief window was enough to audibly/tactilely cut the
    /// buzz out. Whether the *zone* is active is a stable question across a self-oscillating
    /// effect's whole hold, so gating on this instead keeps grip suppressed continuously
    /// through the entire hold rather than flickering on every sine zero-crossing.</summary>
    public bool ZoneContainsSweep(byte fromPosition, byte toPosition)
    {
        if (_kind == Kind.Off) return false;

        SweptZoneRange(fromPosition, toPosition, out int lo, out int hi);

        if (_zoneForce is not null) return MaxZoneForceInRange(lo, hi) > 0;

        // Machine/Galloping - no per-zone array, just the start/end range.
        return hi >= _startZone && lo <= _endZone;
    }

    /// <summary>raw is the 11-byte blob exactly as HIDMaestro hands it back (mode byte at
    /// [0], parameters after).</summary>
    public static TriggerEffectSample Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 11) return Off;
        byte[] rawCopy = raw.ToArray();

        return raw[0] switch
        {
            // Real DualSense firmware's explicit "no resistance" mode (TriggerEffectType.Off =
            // 0x05) - distinct from 0x00, which is just an all-zero/never-written report and
            // already falls through to Off below.
            0x05 => new TriggerEffectSample(rawCopy, Kind.Off),

            // Feedback (0x21): per-zone strength, no frequency parameter - purely static.
            0x21 => FromZoneBitpack(rawCopy, Kind.Static),

            // Bow (0x22): two zones (start/end) mark a range, raw[3] packs strength (bits 0-2)
            // and snap-force (bits 3-5) each as a 0-7 value meaning 1-8. The snap only fires at
            // full draw on real hardware; approximated here as a stronger pull at the end zone
            // rather than a genuine release, since the Pangu can't reproduce a real snap.
            0x22 => FromBow(rawCopy),

            // Weapon (0x25): two zones (start/end) mark a range at one flat strength - a "wall"
            // resistance. Purely static, no time component in the real protocol either.
            0x25 => FromWeaponRange(rawCopy),

            // Vibration (0x26): per-zone amplitude plus a frequency byte at raw[9] (Hz) - the
            // trigger buzzes at that rate for as long as it's held past the effect's position.
            0x26 => FromZoneBitpack(rawCopy, Kind.Vibration, frequencyHz: rawCopy[9]),

            // Galloping (0x23): NOT a strength value at raw[3] - it packs two timing offsets
            // (firstFoot bits 3-5, secondFoot bits 0-2), the phase within each cycle at which
            // two rhythmic beats fire (modeling hoofbeats). raw[4] is the frequency (Hz) of the
            // whole cycle. There is no amplitude parameter for this mode at all.
            0x23 => FromGalloping(rawCopy),

            // Machine (0x27): raw[3] packs two amplitudes, amplitudeA (bits 0-2) and amplitudeB
            // (bits 3-5) - alternates between them. Unlike every other mode, these are raw 0-7
            // values with NO +1 offset (confirmed by duaLib's own range check being > 7, not
            // > 8 like Weapon/Bow/Vibration/Feedback). raw[4] = frequency (Hz), raw[5] = period
            // (alternation period between A and B, in tenths of a second).
            0x27 => FromMachine(rawCopy),

            _ => new TriggerEffectSample(rawCopy, Kind.Off),
        };
    }

    /// <summary>Approximate 0-255 force this effect implies as the physical trigger moves from
    /// fromPosition to toPosition (0-255, same domain as GamepadState.LeftTrigger/RightTrigger)
    /// since this exact effect started (elapsedSeconds, restarts only on a genuine change - see
    /// IsSameEffectAs). Takes the whole swept range, not just toPosition, because the gate loop
    /// only samples position once every ~16ms - a fast trigger release can cross an effect's
    /// zone (as narrow as ~25 of the 255 positions) entirely between two samples, so checking
    /// only the latest single point can miss a real, felt crossing altogether (confirmed via
    /// live capture: a full pull-and-release that never landed a sample inside the zone produced
    /// zero force for its whole duration, while an otherwise-identical pull a moment later
    /// happened to land one sample inside it and buzzed normally). Called continuously as the
    /// trigger moves/time passes, not just when the effect itself changes, so the simulated
    /// buzz tracks both the pull and, for the self-oscillating modes, the rhythm in real
    /// time.</summary>
    public byte ForceAt(byte fromPosition, byte toPosition, double elapsedSeconds) => _kind switch
    {
        Kind.Static => StaticForceAt(fromPosition, toPosition),
        Kind.Vibration => VibrationForceAt(fromPosition, toPosition, elapsedSeconds),
        Kind.Machine => MachineForceAt(fromPosition, toPosition, elapsedSeconds),
        Kind.Galloping => GallopingForceAt(fromPosition, toPosition, elapsedSeconds),
        _ => 0,
    };

    // Static zones don't vary over time, so a swept crossing is unambiguous: take the strongest
    // zone anywhere between fromPosition and toPosition, inclusive. Guards a null _zoneForce
    // (should only happen from a torn cross-thread struct read - see PanguEngine's
    // _lastLeftTriggerEffect/_lastRightTriggerEffect doc comment - since every Static-kind
    // instance is otherwise always constructed with a zoneForce array): treating that as "no
    // force this tick" is cheap and self-corrects on the next tick, a few milliseconds later.
    private byte StaticForceAt(byte fromPosition, byte toPosition)
    {
        if (_zoneForce is null) return 0;
        SweptZoneRange(fromPosition, toPosition, out int lo, out int hi);
        return MaxZoneForceInRange(lo, hi);
    }

    // Sine modulation (0 to peak and back) at the decoded frequency - matches PadForge's own
    // trigger-effect visualizer, which explicitly renders Vibration as a sine wave rather than
    // a hard on/off buzz (PadForge.App/Views/Controls/TriggerEffectGraph.xaml.cs). Peak is the
    // strongest zone anywhere in the swept range, same reasoning as StaticForceAt; the sine
    // factor itself is evaluated at the current moment regardless of exactly where in the sweep
    // the peak zone was touched, which is an approximation but keeps the modulation continuous.
    // See StaticForceAt's doc comment for why a null _zoneForce is guarded rather than asserted.
    private byte VibrationForceAt(byte fromPosition, byte toPosition, double elapsedSeconds)
    {
        if (_zoneForce is null) return 0;
        SweptZoneRange(fromPosition, toPosition, out int lo, out int hi);
        byte peak = MaxZoneForceInRange(lo, hi);
        if (peak == 0 || _frequencyHz == 0) return 0;
        return (byte)(peak * SineFactor(_frequencyHz, elapsedSeconds));
    }

    // Shared by StaticForceAt/VibrationForceAt (need the actual peak force) and
    // ZoneContainsSweep (only needs "was anything in range nonzero") so the per-zone walk over a
    // swept range exists in one place rather than three copies that have to stay in sync by hand.
    // Caller is responsible for the null check - _zoneForce is unused (null) for Machine/Galloping.
    private byte MaxZoneForceInRange(int lo, int hi)
    {
        byte max = 0;
        for (int zone = lo; zone <= hi; zone++) max = Math.Max(max, _zoneForce![zone]);
        return max;
    }

    // Two nested clocks: a slow square-wave alternation between amplitudeA/amplitudeB every
    // (period/10) seconds - literally what Nielk1's doc comment says "period" means - with the
    // currently-active amplitude further sine-modulated at frequencyHz, the same way Vibration
    // is ("this effect resembles Vibration but will oscillate between two amplitudes" per the
    // same doc comment). The two parameters aren't documented as interacting more precisely
    // than that, so this is this decoder's own synthesis of the two descriptions, not something
    // pulled from a single authoritative formula. Zone gating uses the swept range - see
    // StaticForceAt.
    private byte MachineForceAt(byte fromPosition, byte toPosition, double elapsedSeconds)
    {
        SweptZoneRange(fromPosition, toPosition, out int lo, out int hi);
        if (hi < _startZone || lo > _endZone || _frequencyHz == 0) return 0;

        double periodSeconds = _periodTenthsSec / 10.0;
        byte baseAmplitude = periodSeconds <= 0
            ? _amplitudeA
            : (elapsedSeconds % periodSeconds) / periodSeconds < 0.5 ? _amplitudeA : _amplitudeB;

        return (byte)(baseAmplitude * SineFactor(_frequencyHz, elapsedSeconds));
    }

    private static void SweptZoneRange(byte fromPosition, byte toPosition, out int lo, out int hi)
    {
        int zoneA = PositionToZone(fromPosition);
        int zoneB = PositionToZone(toPosition);
        lo = Math.Min(zoneA, zoneB);
        hi = Math.Max(zoneA, zoneB);
    }

    // No amplitude parameter exists for this mode at all - real hardware apparently uses a
    // fixed/implicit pulse strength for each "hoofbeat". Approximated here as two brief,
    // decaying pulses per cycle (default full-strength hits) at the phase positions
    // firstFoot/8 and secondFoot/8 through each 1/frequencyHz-second cycle. The decay window
    // (PulseDecayMs) is this decoder's own choice, not a documented value - it exists so a
    // beat is reliably felt across the ~16ms gate-loop tick spacing (PanguEngine.RumbleGateLoop)
    // instead of risking landing between two samples, the same problem
    // AppSettings.AdaptiveTriggerReleaseMs solves for the static modes; Galloping isn't in that
    // release-time cohort because it already supplies its own beat-to-beat timing, not because
    // its individual pulses don't need any decay tail at all.
    private const double GallopingPulseDecayMs = 80.0;
    private const double GallopingPulseForce = 255.0;

    private byte GallopingForceAt(byte fromPosition, byte toPosition, double elapsedSeconds)
    {
        SweptZoneRange(fromPosition, toPosition, out int lo, out int hi);
        if (hi < _startZone || lo > _endZone || _frequencyHz == 0) return 0;

        double periodSeconds = 1.0 / _frequencyHz;
        double cyclePosition = elapsedSeconds % periodSeconds;

        double force = Math.Max(
            PulseForceAt(cyclePosition, _firstFootPhase / 8.0 * periodSeconds, periodSeconds),
            PulseForceAt(cyclePosition, _secondFootPhase / 8.0 * periodSeconds, periodSeconds));
        return (byte)Math.Clamp(force, 0, 255);
    }

    private static double PulseForceAt(double cyclePosition, double beatTime, double periodSeconds)
    {
        double dtMs = (cyclePosition - beatTime) * 1000.0;
        if (dtMs < 0) dtMs += periodSeconds * 1000.0; // beat landed in the previous cycle, may still be decaying
        if (dtMs > GallopingPulseDecayMs) return 0;
        return GallopingPulseForce * Math.Exp(-dtMs / (GallopingPulseDecayMs / 3.0));
    }

    private static double SineFactor(byte frequencyHz, double elapsedSeconds) =>
        0.5 + 0.5 * Math.Sin(2.0 * Math.PI * frequencyHz * elapsedSeconds);

    private static int PositionToZone(byte position) => Math.Clamp(position * ZoneCount / 256, 0, ZoneCount - 1);

    /// <summary>1-8 protocol strength to 0-255 Pangu force domain - the real DualSense's
    /// intensity resolution (confirmed via dualsensectl and duaLib both), not the full width of
    /// the byte carrying it. Used by every mode except Machine, whose amplitudes are raw 0-7
    /// with no +1 offset - see ScaleRaw0To7.</summary>
    private static byte ScaleStrength(int strength1To8) =>
        (byte)(Math.Clamp(strength1To8, 0, 8) * 255 / 8);

    /// <summary>Machine-only: amplitudeA/amplitudeB are raw 0-7 values, unlike every other
    /// mode's 1-8-via-plus-one convention (see the Decode 0x27 case comment).</summary>
    private static byte ScaleRaw0To7(int value0To7) =>
        (byte)(Math.Clamp(value0To7, 0, 7) * 255 / 7);

    // Bit-packed per-zone strength shared by Feedback (0x21) and Vibration (0x26): active_zones
    // is a 10-bit mask (raw[1]/raw[2]), strength_zones packs a 0-7 value (meaning 1-8) into 3
    // bits per zone across raw[3..6].
    private static TriggerEffectSample FromZoneBitpack(byte[] raw, Kind kind, byte frequencyHz = 0)
    {
        int activeZones = raw[1] | (raw[2] << 8);
        uint strengthZones = (uint)(raw[3] | (raw[4] << 8) | (raw[5] << 16) | (raw[6] << 24));

        var zoneForce = new byte[ZoneCount];
        bool any = false;
        for (int i = 0; i < ZoneCount; i++)
        {
            if ((activeZones & (1 << i)) == 0) continue;
            int strength = (int)((strengthZones >> (3 * i)) & 0x07) + 1;
            zoneForce[i] = ScaleStrength(strength);
            any = true;
        }

        return any ? new TriggerEffectSample(raw, kind, zoneForce: zoneForce, frequencyHz: frequencyHz)
                   : new TriggerEffectSample(raw, Kind.Off);
    }

    private static TriggerEffectSample FromWeaponRange(byte[] raw)
    {
        if (!TryGetZoneRange(raw, out int startZone, out int endZone)) return new TriggerEffectSample(raw, Kind.Off);

        byte force = ScaleStrength((raw[3] & 0x07) + 1);

        var zoneForce = new byte[ZoneCount];
        for (int i = startZone; i <= endZone; i++) zoneForce[i] = force;
        return new TriggerEffectSample(raw, Kind.Static, zoneForce: zoneForce);
    }

    private static TriggerEffectSample FromBow(byte[] raw)
    {
        if (!TryGetZoneRange(raw, out int startZone, out int endZone)) return new TriggerEffectSample(raw, Kind.Off);

        byte forcePair = raw[3];
        byte pullForce = ScaleStrength((forcePair & 0x07) + 1);
        byte snapForce = ScaleStrength(((forcePair >> 3) & 0x07) + 1);

        var zoneForce = new byte[ZoneCount];
        for (int i = startZone; i <= endZone; i++) zoneForce[i] = pullForce;
        zoneForce[endZone] = snapForce;
        return new TriggerEffectSample(raw, Kind.Static, zoneForce: zoneForce);
    }

    private static TriggerEffectSample FromMachine(byte[] raw)
    {
        if (!TryGetZoneRange(raw, out int startZone, out int endZone)) return new TriggerEffectSample(raw, Kind.Off);

        byte amplitudeA = ScaleRaw0To7(raw[3] & 0x07);
        byte amplitudeB = ScaleRaw0To7((raw[3] >> 3) & 0x07);
        byte frequencyHz = raw[4];
        byte periodTenthsSec = raw[5];

        if (frequencyHz == 0) return new TriggerEffectSample(raw, Kind.Off);

        return new TriggerEffectSample(raw, Kind.Machine,
            startZone: (byte)startZone, endZone: (byte)endZone,
            amplitudeA: amplitudeA, amplitudeB: amplitudeB,
            frequencyHz: frequencyHz, periodTenthsSec: periodTenthsSec);
    }

    private static TriggerEffectSample FromGalloping(byte[] raw)
    {
        if (!TryGetZoneRange(raw, out int startZone, out int endZone)) return new TriggerEffectSample(raw, Kind.Off);

        byte secondFootPhase = (byte)(raw[3] & 0x07);
        byte firstFootPhase = (byte)((raw[3] >> 3) & 0x07);
        byte frequencyHz = raw[4];

        if (frequencyHz == 0) return new TriggerEffectSample(raw, Kind.Off);

        return new TriggerEffectSample(raw, Kind.Galloping,
            startZone: (byte)startZone, endZone: (byte)endZone,
            firstFootPhase: firstFootPhase, secondFootPhase: secondFootPhase, frequencyHz: frequencyHz);
    }

    // start_stop_zones is a 16-bit mask with exactly two bits set (the start and end position
    // zones, 0-9 each) - shared byte layout across Weapon/Bow/Galloping/Machine.
    private static bool TryGetZoneRange(byte[] raw, out int startZone, out int endZone)
    {
        int mask = raw[1] | (raw[2] << 8);
        startZone = -1;
        endZone = -1;
        for (int i = 0; i < ZoneCount; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            if (startZone < 0) startZone = i;
            endZone = i;
        }
        return startZone >= 0 && endZone >= startZone;
    }

    private TriggerEffectSample(
        byte[] raw, Kind kind,
        byte[]? zoneForce = null,
        byte startZone = 0, byte endZone = 0,
        byte amplitudeA = 0, byte amplitudeB = 0,
        byte frequencyHz = 0, byte periodTenthsSec = 0,
        byte firstFootPhase = 0, byte secondFootPhase = 0)
    {
        _raw = raw;
        _kind = kind;
        _zoneForce = zoneForce;
        _startZone = startZone;
        _endZone = endZone;
        _amplitudeA = amplitudeA;
        _amplitudeB = amplitudeB;
        _frequencyHz = frequencyHz;
        _periodTenthsSec = periodTenthsSec;
        _firstFootPhase = firstFootPhase;
        _secondFootPhase = secondFootPhase;
    }
}
