using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PanguBridge.Mapping;

namespace PanguBridge;

public enum HmConnectMode
{
    /// <summary>Create the virtual DualSense Edge immediately when the engine starts.</summary>
    OnStart,

    /// <summary>Defer creation until the physical Pangu controller is first detected.
    /// Steam won't see the virtual controller at all until the dongle is connected.</summary>
    OnControllerConnect,
}

public enum VirtualBackend
{
    /// <summary>HIDMaestro DualSense Edge - bundled DLL, real proportional rumble from Steam.</summary>
    HidMaestro,

    /// <summary>vJoy - external driver install required, no proportional rumble
    /// (Steam sends only an on/off signal to generic DirectInput devices).</summary>
    VJoy,
}

/// <summary>
/// Which physical motors respond to Steam's rumble signal - grip and trigger are two separate
/// hardware subsystems on the Pangu, not two names for the same thing (see
/// HidReader.TrySendMotors for how they're combined into one report). "IfPulled" modes gate
/// the trigger motors on whether that side's physical trigger analog is currently pulled at
/// all (> 0), independent of whatever grip signal Steam is sending; see
/// PanguEngine.ApplyRumbleOutput.
/// </summary>
public enum RumbleMode
{
    /// <summary>Only grip motors rumble; trigger motors stay off.</summary>
    GripOnly,

    /// <summary>Only trigger motors rumble; grip motors stay off.</summary>
    TriggerOnly,

    /// <summary>Both grip and trigger motors rumble together, unconditionally.</summary>
    GripAndTrigger,

    /// <summary>Grip motors always rumble. Each trigger motor independently rumbles too, but
    /// only while its own physical trigger is pulled at all.</summary>
    GripAndTriggerIfPulled,

    /// <summary>Per side, independently: if that side's physical trigger is pulled, only its
    /// trigger motor rumbles (grip suppressed for that side); otherwise only its grip motor
    /// rumbles.</summary>
    GripOrTriggerIfPulled,
}

/// <summary>How Audio Auto Haptics' derived rumble signal combines with whatever a motor group
/// would otherwise be doing under RumbleMode, for one motor group (grip or trigger). See
/// PanguEngine.ApplyRumbleOutput - only ever applies to a motor group RumbleMode has already
/// made active; it never turns on a group RumbleMode excludes. Adaptive Trigger Simulation, when
/// on, overrides this entirely for the trigger group regardless of this setting.</summary>
public enum AudioAutoHapticsMode
{
    /// <summary>Audio Auto Haptics has no effect on this motor group.</summary>
    NoAudioRumble,

    /// <summary>This motor group's rumble comes entirely from the audio-derived signal instead
    /// of whatever RumbleMode/Steam would have sent it.</summary>
    Replace,

    /// <summary>The audio-derived signal is added on top of whatever RumbleMode/Steam would
    /// have sent this motor group, clamped to the valid range rather than allowed to overflow.</summary>
    NormalPlusAudio,
}

/// <summary>Persisted app settings. Everything PanguBridge saves lives in this one file
/// (ConfigPaths.SettingsFile), including the vJoy button mapping (VJoyButtonMap property
/// below) - there is no separate profiles folder.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>When to create the HIDMaestro virtual DualSense Edge controller.</summary>
    public HmConnectMode HmConnectMode { get; set; } = HmConnectMode.OnStart;

    /// <summary>Off by default. When on, the virtual controller is torn down the moment the
    /// physical Pangu disconnects and recreated the moment it reconnects, independent of
    /// HmConnectMode - see PanguEngine.OnHidConnectionChanged.</summary>
    public bool HmDisconnectOnControllerDisconnect { get; set; }

    /// <summary>How often (Hz) HidMaestroOutput's dedicated submit thread sends a fresh state
    /// to HIDMaestro's driver - separate from how often the driver itself polls (a fixed
    /// ~250 Hz regardless of this value; see HidMaestroOutput.SubmitThreadProc). One of 250,
    /// 500, 750, or 1000. Lower trims a small amount of CPU use at the cost of slightly less
    /// precise input timing.</summary>
    public int HmSubmitRateHz { get; set; } = 1000;

    /// <summary>Which virtual controller driver PanguBridge uses as its output.
    /// HidMaestro (default) bundles HIDMaestro.Core.dll and presents as a DualSense Edge,
    /// giving real proportional rumble. VJoy requires a separate vJoy install and only
    /// delivers on/off rumble via Steam Input's generic DirectInput FFB path.</summary>
    public VirtualBackend Backend { get; set; } = VirtualBackend.HidMaestro;

    public bool HidHideEnabled { get; set; } = true;
    public bool AutoHidHide { get; set; } = true;
    public bool AutostartEnabled { get; set; }

    /// <summary>Manual override for vJoy's install directory, set via the "Locate vJoy
    /// manually" button when auto-detection (registry + conventional paths) can't find it.</summary>
    public string? VJoyInstallDir { get; set; }

    /// <summary>Checking this asks for literally-inverted Y-axis output on the left stick -
    /// see PanguEngine.ComputeYAxis. Off by default, which is the corrected (non-inverted) feel.</summary>
    public bool InvertLeftStickY { get; set; }

    /// <summary>Same as <see cref="InvertLeftStickY"/> but for the right stick.</summary>
    public bool InvertRightStickY { get; set; }

    /// <summary>X already needs a mandatory hardware-correction flip regardless of this
    /// setting (the raw byte runs high-to-low) - checking this asks for an additional flip on
    /// top of that correction, i.e. "inverted relative to the normal/correct feel," the same
    /// relationship InvertLeftStickY has to Y. Off by default.</summary>
    public bool InvertLeftStickX { get; set; }

    /// <summary>Same as <see cref="InvertLeftStickX"/> but for the right stick.</summary>
    public bool InvertRightStickX { get; set; }

    /// <summary>Redirects the left stick's (post-inversion) output to the right stick's output
    /// axis and vice versa. Inversion settings above always apply to the physical stick they
    /// name, regardless of this - swapping happens after inversion. Applies to both backends.</summary>
    public bool SwapSticks { get; set; }

    /// <summary>Same idea as <see cref="SwapSticks"/> but for the two triggers.</summary>
    public bool SwapTriggers { get; set; }

    /// <summary>"Dark" (default) or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Which vJoy device (1-16) PanguBridge acquires and drives. Most setups only
    /// ever configure device 1, but this is changeable for users running other vJoy-based
    /// tools that already claim device 1.</summary>
    public int VJoyDeviceId { get; set; } = 1;

    /// <summary>Which physical motors respond to Steam's rumble signal.</summary>
    public RumbleMode RumbleMode { get; set; } = RumbleMode.GripAndTriggerIfPulled;

    /// <summary>Grip motor intensity caps, 0-100 (percentage of the raw 0-255 magnitude Steam
    /// sends). 100 = no reduction.</summary>
    public int GripLeftIntensity { get; set; } = 100;
    public int GripRightIntensity { get; set; } = 100;

    /// <summary>Trigger motor intensity caps, 0-4 (the trigger subsystem's own discrete level
    /// scale - see HidReader.TrySendMotors). 4 = no reduction, and is the confirmed real max -
    /// the wire byte's high nibble has unused room up to 15, but nothing above 4 increases
    /// strength and 5 specifically produces a wrong-feeling buzz.</summary>
    public int TriggerLeftIntensity { get; set; } = 4;
    public int TriggerRightIntensity { get; set; } = 4;

    /// <summary>Derives rumble from whatever audio the PC is currently playing (bass +
    /// transients), for games that don't send any rumble of their own. Off by default - an
    /// approximation, not literal per-game haptic content; see docs/rumble.md. Applies per
    /// motor group via AudioAutoHapticsGripMode/AudioAutoHapticsTriggerPulledMode/
    /// AudioAutoHapticsTriggerIdleMode, gated by RumbleMode and overridden entirely by
    /// AdaptiveTriggerSimulation for the trigger group.</summary>
    public bool AudioAutoHapticsEnabled { get; set; } = false;

    /// <summary>Which audio render device to capture. Null/empty = the current Windows default
    /// output device (Multimedia role), tracked live as the default changes. The literal string
    /// "communications" selects the Default Communication Device (Communications role) instead,
    /// tracked the same way. Any other value is a specific MMDevice ID, pinned regardless of
    /// what Windows currently considers default.</summary>
    public string? AudioAutoHapticsDeviceId { get; set; }

    public AudioAutoHapticsMode AudioAutoHapticsGripMode { get; set; } = AudioAutoHapticsMode.NoAudioRumble;

    /// <summary>Trigger motors can use a different Audio Auto Haptics mode depending on whether
    /// that side's physical trigger is currently pulled (> 0) or idle - see
    /// PanguEngine.ApplyRumbleOutput. Defaults to blending normal rumble with audio while
    /// pulled, and audio only while idle.</summary>
    public AudioAutoHapticsMode AudioAutoHapticsTriggerPulledMode { get; set; } = AudioAutoHapticsMode.NormalPlusAudio;
    public AudioAutoHapticsMode AudioAutoHapticsTriggerIdleMode { get; set; } = AudioAutoHapticsMode.Replace;

    /// <summary>Advanced DSP tuning - see AudioAutoHapticsCapture. Only bass below this
    /// frequency drives vibration.</summary>
    public double AudioAutoHapticsCutoffHz { get; set; } = 160.0;

    /// <summary>How quickly vibration ramps up when a sudden sound hits.</summary>
    public double AudioAutoHapticsAttackMs { get; set; } = 1.0;

    /// <summary>How long vibration takes to fade after a sound ends.</summary>
    public double AudioAutoHapticsReleaseMs { get; set; } = 80.0;

    /// <summary>How strongly loud moments are emphasized over quiet ones.</summary>
    public double AudioAutoHapticsIntensityBoost { get; set; } = 3.0;

    /// <summary>When on (default), a subwoofer/LFE channel - if the capture device's format
    /// declares one (5.1/7.1 setups) - is folded into both the left and right vibration
    /// signals alongside every other channel the format declares. Off skips the LFE channel
    /// specifically; all non-LFE channels (front, center, rear/side, etc.) are still used
    /// either way.</summary>
    public bool AudioAutoHapticsIncludeLfe { get; set; } = true;

    /// <summary>When on (default), a dedicated center channel - if the capture device's format
    /// declares one - is folded into both the left and right vibration signals alongside every
    /// other channel the format declares. Off skips the center channel specifically; all
    /// non-center channels (front left/right, LFE, rear/side, etc.) are still used either
    /// way.</summary>
    public bool AudioAutoHapticsIncludeCenter { get; set; } = true;

    /// <summary>Noise gates, 0-100 each (percent of the fully soft-clipped output range - see
    /// AudioAutoHapticsCapture.OnDataAvailable and PanguEngine.ApplyRumbleOutput). Whichever
    /// motor group a given audio sample would drive, if that group's floor isn't cleared the
    /// sample is forced to 0 instead of producing a small nonzero rumble, so quiet ambience/
    /// background audio doesn't keep the motors lightly buzzing. Split per motor group (rather
    /// than one shared floor) since grip and trigger, and a trigger's pulled vs idle state,
    /// warrant different sensitivity. 0 disables that group's gate entirely.</summary>
    public double AudioAutoHapticsGripNoiseFloor { get; set; } = 35.0;
    public double AudioAutoHapticsTriggerPulledNoiseFloor { get; set; } = 10.0;
    public double AudioAutoHapticsTriggerIdleNoiseFloor { get; set; } = 35.0;

    /// <summary>When on, the trigger motors stop following RumbleMode's generic rumble routing
    /// and instead buzz based on the game's real DualSense adaptive-trigger effect data,
    /// evaluated live against the physical trigger's own pull position - see
    /// AdaptiveTriggerEffect and PanguEngine.ApplyRumbleOutput. Grip motors are unaffected and
    /// keep following RumbleMode as usual. The Pangu has no real trigger-resistance hardware,
    /// so this only ever approximates the *feel* via buzz intensity, never actual resistance -
    /// off by default since it's a best-effort approximation, not a faithful reproduction.</summary>
    public bool AdaptiveTriggerSimulation { get; set; } = false;

    /// <summary>When on, adaptive-trigger-simulated levels skip the TriggerLeftIntensity/
    /// TriggerRightIntensity caps above and can reach full strength (4) regardless of what
    /// those sliders are set to. Only has an effect while AdaptiveTriggerSimulation is on.
    /// Defaults to on, so turning AdaptiveTriggerSimulation on gives the best experience out
    /// of the box.</summary>
    public bool AdaptiveTriggerIgnoreIntensity { get; set; } = true;

    /// <summary>Workaround for a confirmed issue where a side's grip motor can cause that same
    /// side's Adaptive Trigger buzz to cut out. When on, a side's grip motor is suppressed
    /// whenever Adaptive Trigger Simulation is actively buzzing that same side's trigger; the
    /// opposite side's grip is unaffected. Only has an effect while AdaptiveTriggerSimulation
    /// is on. Defaults to on, so turning AdaptiveTriggerSimulation on gives the best experience
    /// out of the box.</summary>
    public bool AdaptiveTriggerDisableMatchingGrip { get; set; } = true;

    /// <summary>Physical button -> vJoy button slot (1-19), or null to decode but not send to
    /// vJoy. Used only by the vJoy (Legacy) backend's Button Mapping tab (MappingEditor); the
    /// HIDMaestro/DualSense Edge path has fixed semantic button positions and ignores this
    /// entirely.</summary>
    public Dictionary<SourceButton, int?> VJoyButtonMap { get; set; } = new();

    /// <summary>Physical digital input -> HIDMaestro DualSense Edge output button, edited via
    /// the HIDMaestro tab's mapping table. Only takes effect when the user clicks "Save and
    /// Apply Mappings" there - not live like the vJoy mapping's Save button either.</summary>
    public Dictionary<HmSourceButton, HmOutputButton> HmButtonMap { get; set; } = HmMapping.CreateDefault();

    public static AppSettings LoadOrCreateDefault()
    {
        var path = ConfigPaths.SettingsFile;
        AppSettings settings;

        if (!File.Exists(path))
        {
            settings = new AppSettings();
        }
        else
        {
            try
            {
                var json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch (Exception)
            {
                // settings.json can end up empty, truncated, hand-edited into invalid JSON, or
                // saved by a build with a different schema - any of which would otherwise crash
                // the app on every launch with no console to explain why. Fall back to defaults.
                settings = new AppSettings();
            }
        }

        Normalize(settings);

        try
        {
            settings.Save();
        }
        catch (Exception)
        {
            // Best-effort - if the directory is unwritable we still want to run with
            // in-memory (and possibly freshly-migrated/defaulted) settings rather than crash.
        }

        return settings;
    }

    /// <summary>Clamps every field to a valid range regardless of what was actually read from
    /// disk (or hand-edited), and seeds VJoyButtonMap with the built-in default mapping if it's
    /// empty. Called on every load, not just first run, so a corrupted/out-of-range value never
    /// reaches VJoyOutput/HidReader.</summary>
    private static void Normalize(AppSettings settings)
    {
        if (settings.Theme is not ("Dark" or "Light")) settings.Theme = "Dark";
        if (settings.VJoyDeviceId is < 1 or > 16) settings.VJoyDeviceId = 1;
        if (settings.HmSubmitRateHz is not (250 or 500 or 750 or 1000)) settings.HmSubmitRateHz = 1000;
        settings.GripLeftIntensity     = Math.Clamp(settings.GripLeftIntensity, 0, 100);
        settings.GripRightIntensity    = Math.Clamp(settings.GripRightIntensity, 0, 100);
        settings.TriggerLeftIntensity  = Math.Clamp(settings.TriggerLeftIntensity, 0, 4);
        settings.TriggerRightIntensity = Math.Clamp(settings.TriggerRightIntensity, 0, 4);
        settings.AudioAutoHapticsCutoffHz       = Math.Clamp(settings.AudioAutoHapticsCutoffHz, 60.0, 400.0);
        settings.AudioAutoHapticsAttackMs       = Math.Clamp(settings.AudioAutoHapticsAttackMs, 0.5, 10.0);
        settings.AudioAutoHapticsReleaseMs      = Math.Clamp(settings.AudioAutoHapticsReleaseMs, 20.0, 300.0);
        settings.AudioAutoHapticsIntensityBoost = Math.Clamp(settings.AudioAutoHapticsIntensityBoost, 1.0, 6.0);
        settings.AudioAutoHapticsGripNoiseFloor          = Math.Clamp(settings.AudioAutoHapticsGripNoiseFloor, 0.0, 100.0);
        settings.AudioAutoHapticsTriggerPulledNoiseFloor = Math.Clamp(settings.AudioAutoHapticsTriggerPulledNoiseFloor, 0.0, 100.0);
        settings.AudioAutoHapticsTriggerIdleNoiseFloor   = Math.Clamp(settings.AudioAutoHapticsTriggerIdleNoiseFloor, 0.0, 100.0);

        settings.VJoyButtonMap ??= new Dictionary<SourceButton, int?>();

        if (settings.VJoyButtonMap.Count > 0)
        {
            // Clamp rather than trust: a hand-edited or corrupted file could otherwise hand
            // VJoyOutput.SetButton a slot number outside the 1-19 range it actually supports.
            foreach (var button in settings.VJoyButtonMap.Keys.ToList())
            {
                if (settings.VJoyButtonMap[button] is int slot && slot is < 1 or > 19)
                    settings.VJoyButtonMap[button] = null;
            }
        }
        else
        {
            settings.VJoyButtonMap = MappingProfile.CreateDefault().ButtonMap;
        }

        if (settings.HmButtonMap is null or { Count: 0 })
            settings.HmButtonMap = HmMapping.CreateDefault();
    }

    public void Save()
    {
        ConfigPaths.EnsureDirectoriesExist();
        File.WriteAllText(ConfigPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
    }
}
