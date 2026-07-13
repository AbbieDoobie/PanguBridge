# Rumble

How game rumble reaches the physical Pangu controller. Rumble is only supported on the
HIDMaestro backend - the vJoy backend is buttons/axes only and deliberately leaves Force
Feedback disabled (see `VJoyConfigCli.TryConfigureDevice`).

Rumble commands go out on Interface 4's existing OUT endpoint (`0x02`), the same stream
`HidReader` already holds open for the enable command and keepalive - no separate interface,
no XInput dependency.

## Grip motors

```
02 16 <LeftMotor> <RightMotor> <LeftMotor> <RightMotor> 00 00 ... 00   (64 bytes)
```

- `byte[0]` = `0x02` (report ID)
- `byte[1]` = `0x16` (rumble command type)
- `byte[2]`/`byte[3]` = Left/Right motor magnitude, `0x00`-`0xFF`, continuous
- `byte[4]`/`byte[5]` = the same (Left, Right) pair, mirrored

## Trigger motors

The controller has four physical motors: two grip (above), two trigger. The trigger motors are
a separate subsystem addressed via a different command, not a continuous stream like grip.

```
0x62 - configure level (sent once per change):
02 62 00 0C 3C 96 A5 C3 01 FF 00 9A 00 <LeftLevel> <RightLevel> 04 00 FF FF 00 00 ... 00

0x16 - fire/stop (same command type grip uses, different byte usage):
02 16 00 00 <LeftFire> <RightFire> 00 ... 00
```

- `byte[13]` (in the `0x62` packet) = Left trigger level × `0x10` - i.e. `0x00, 0x10, 0x20,
  0x30, 0x40` for levels 0-4. 4 is the confirmed real maximum, not merely a UI-imposed limit:
  the byte has unused headroom up to 15 (a 4-bit nibble), but level 5 produces a distinctly
  wrong-feeling buzz and nothing above 4 increases strength.
- `byte[14]` = Right trigger level × `0x10` **+ `0x04`**. The `+0x04` only appears on the right
  side; why the two sides use a different additive constant is unconfirmed.
- Every other byte in the `0x62` packet is constant across every observed trigger-only write -
  reproduced as a fixed preamble (`HidReader.ConfigPreamble`, bytes `[2]-[8]`) since its meaning
  is unconfirmed. Bytes `[9]-[12]` of this same report also carry LED state - see
  `docs/led.md`.
- `byte[4]`/`byte[5]` (in the **separate** `0x16` fire packet) = `0xFF` to start Left/Right
  trigger at whatever level was last configured via `0x62`, `0x00` to stop it. A side's
  configured level is inert until that side's fire bit is set, independent of the other side's
  level or fire state.

### Combined send

Grip and the trigger fire packet share the same report type (`0x16`) and overlapping byte
ranges (grip: `[2]`/`[3]` real, mirrored into `[4]`/`[5]`; trigger fire: `[4]`/`[5]` set,
`[2]`/`[3]` zeroed). The device has no concept of a partial update - each `0x16` write is a
complete overwrite of all 4 bytes, not a merge. `HidReader.TrySendMotors(byte gripLeft, byte
gripRight, int triggerLeftLevel, int triggerRightLevel)` builds one combined report per call:
grip's real magnitude always goes in `[2]`/`[3]`; `[4]`/`[5]` gets the trigger fire flag
(`0xFF`) when that side's trigger should be firing, otherwise it mirrors grip's own magnitude.
The `0x62` configure packet is sent whenever the trigger level changes.

### Periodic refresh requirement

The `0x16` trigger-fire flag is not a state the firmware holds indefinitely - it needs to be
refreshed periodically, the same reason the device's general session keepalive (a separate
report type, sent every 250ms) has to repeat or the device stops reporting extra buttons
entirely. `PanguEngine.ApplyRumbleOutput` force-resends whenever any motor is meant to be
active and more than `MotorRefreshInterval` (100ms) has passed since the last write, regardless
of whether the computed values changed - this applies to grip as well as trigger, since any
RumbleMode holding a perfectly steady nonzero value for longer than 100ms is exposed to the
same requirement.

### DualSense output-report valid flags

Steam/games communicate with the virtual DualSense Edge controller using the real DualSense
output-report protocol, which is a full-buffer replace, not a delta - every write carries every
field's bytes regardless of whether that particular write means to change them. The sender
signals *intent* to change a given section via `validFlag0`/`validFlag1`/`validFlag2` bitmasks
(confirmed against `dualsensectl`'s source: its `command_trigger` sets
`DS_OUTPUT_VALID_FLAG0_RIGHT/LEFT_TRIGGER_MOTOR_ENABLE`, `0x04`/`0x08`, while its vibration
command never touches those bits). `HidMaestroOutput.OnOutputDecoded` reads `validFlag0` and
only fires `AdaptiveTriggerChanged` for a side whose corresponding bit is actually set in a
given write; `PanguEngine.OnAdaptiveTriggerChanged` only updates its cached effect for a
non-null side, leaving the other side's cached effect untouched. Without this, a rumble-only
write whose trigger-effect bytes happen to be stale/zeroed would look identical to the game
explicitly turning the effect off.

## Adaptive Trigger Simulation (HIDMaestro path only)

The real DualSense Edge has motorized trigger resistance ("Adaptive Triggers") - the Pangu does
not. This feature drives the Pangu's existing trigger rumble motors (the 0-4 discrete-level
subsystem above) from the game's adaptive-trigger resistance data, so pulling the trigger past
a resistance point produces a buzz. It approximates the *feel* via buzz intensity; it cannot
reproduce actual resistance, since the Pangu has no hardware for that.

### Data source

HIDMaestro's `dualsense-edge` profile declares two `bytes-passthrough` fields on the output
report (`extendedOutputReport`, report ID `0x02`): `rightTriggerEffect` (bytes 11-21) and
`leftTriggerEffect` (bytes 22-32), 11 bytes each. HIDMaestro passes back the raw bytes exactly
as the game/Steam sent them, the same as it does for `leftMotor`/`rightMotor` (grip rumble).
`HidMaestroOutput.OnOutputDecoded` reads these two fields and raises `AdaptiveTriggerChanged`.

### Effect byte layout

Byte 0 of each 11-byte blob is a mode ID; the rest are mode-specific parameters. Mode IDs and
every parameter layout are confirmed against `Nielk1/duaLib`'s `triggerFactory.cpp`/`.h`
(github.com/WujekFoliarz/duaLib, MIT licensed, explicitly revision-numbered and
parameter-validated) - the same source both PadForge's own DualSense encoder and `dualsensectl`
independently trace back to. duaLib's own enum splits modes into two groups: "officially
recognized" (documented, expected to keep working in future firmware) and "unofficial but
unique effects left in the firmware" (undocumented; Galloping and Machine were specifically
reverse-engineered from Metro Exodus's own HID traffic). Live capture against a real game
(Steam Input off, so nothing translated the bytes) additionally confirmed Off/Bow/Weapon/
Vibration byte-for-byte.

An earlier version of this decoder was built against `Ohjurot/DualSense-Windows`'s
`DS5_Output.cpp`, a different (and, per live capture, incorrect for real games) mode-ID scheme -
that library's `0x00`/`0x01`/`0x02` IDs never actually appeared in captured traffic.

`AdaptiveTriggerEffect.cs` decodes:

- `0x05` Off - no resistance requested (distinct from an all-zero/never-written report, which
  also decodes to Off).
- `0x21` Feedback - per-zone strength (10 position zones spanning the full 0-255 pull range,
  3-bit packed strength per zone meaning 1-8). Static, no time component.
- `0x25` Weapon - a "wall": one flat strength across a start/end zone range. Static.
- `0x22` Bow - a start/end zone range with a pull strength across it, plus a stronger "snap"
  force placed on the end zone only (approximates the snap at full draw a real Bow effect has -
  the Pangu can't reproduce an actual snap release). Static.
- `0x26` Vibration - per-zone amplitude (same packing as Feedback) plus a frequency byte (Hz).
  Genuinely time-varying: the trigger buzzes at that rate for as long as it's held past the
  effect's position, rendered as a sine-modulated amplitude (0 to peak and back) - matches how
  PadForge's own trigger-effect visualizer renders this mode.
- `0x27` Machine - a start/end zone range, two alternating force levels (`amplitudeA`/
  `amplitudeB`, raw 0-7 with *no* +1 offset - the one mode that differs from every other mode's
  1-8-via-plus-one convention, confirmed by duaLib's own range check being `> 7` not `> 8`), a
  frequency byte (Hz), and a period byte (alternation period between A and B, in *tenths of a
  second* per duaLib's own doc comment). Rendered as a square-wave alternation between A and B
  at the period's rate, with whichever is currently active further sine-modulated at the
  frequency - two nested clocks, since duaLib documents the two parameters as controlling
  different things ("resembles Vibration" for the buzz, "period of oscillation between A and B"
  for the swing) without spelling out how they combine; this is `AdaptiveTriggerEffect.cs`'s own
  synthesis of those two descriptions, not a documented formula.
- `0x23` Galloping - a start/end zone range and two timing offsets (`firstFoot`/`secondFoot`,
  0-6/1-7) marking phase positions within a cycle, plus a frequency byte (Hz) for the whole
  cycle rate - modeling two hoofbeats per cycle. There is no amplitude parameter for this mode
  at all; rendered as two brief, decaying pulses per cycle at a fixed/full assumed strength
  (`GallopingPulseForce`), since the protocol carries no data to derive a magnitude from. The
  decay window (`GallopingPulseDecayMs`, 80ms) is this decoder's own choice - it exists so a
  beat lands reliably across the ~16ms gate-loop tick spacing rather than risking falling
  between two samples.
- Anything else (including `0xFC` Calibrate and its `0xFD`/`0xFE` neighbors) is treated as Off.

`TriggerEffectSample.IsSelfOscillating` is true for Vibration/Machine/Galloping - see
`AdaptiveTriggerReleaseMs` below for why that distinction matters.

### Live evaluation

A game sends a new trigger-effect packet only when the effect changes, not on every trigger
movement, so the resistance curve has to be re-evaluated continuously against the physical
trigger's current position (and, for the self-oscillating modes, elapsed time). `PanguEngine`
caches the latest decoded `TriggerEffectSample` per side and re-evaluates `ForceAt` on the same
~60 Hz rumble gate loop the `...IfPulled` RumbleModes use to react to physical trigger
pull/release between Steam packets (`PanguEngine.RumbleGateLoop`), so the buzz tracks both the
pull and, where relevant, the effect's own rhythm in real time.

`ForceAt(fromPosition, toPosition, elapsedSeconds)` takes the whole position swept since the
previous gate-loop tick, not just a single current-position lookup - confirmed via live capture
to matter in practice: a fast trigger pull-and-release can cross an effect's zone (as narrow as
~25 of the 255 positions) entirely within one ~16ms tick, so checking only the latest sampled
position can miss a real, felt crossing altogether even though the trigger genuinely passed
through the zone. `PanguEngine` tracks each physical side's previous raw position
(`_lastLeftTriggerPosition`/`_lastRightTriggerPosition`) and feeds both endpoints through
`AdaptiveTriggerTimingOffsetPercent` before the sweep.

`elapsedSeconds` is time since the *current* effect started, tracked per side via
`Stopwatch.GetTimestamp()` in `PanguEngine.OnAdaptiveTriggerChanged`. Games/Steam routinely
resend an unchanged effect on every rumble refresh (confirmed via live capture) - naively
resetting this timer on every notification would restart Vibration/Machine/Galloping's
oscillation phase on every resend, producing an audible/tactile stutter instead of a smooth
buzz. `TriggerEffectSample.IsSameEffectAs` (byte-for-byte comparison of the original 11-byte
blob) gates the reset so it only happens on a genuine change. The same effect-identity check
also forces `PanguEngine.ApplyAdaptiveTriggerRelease` to snap instantly whenever the *effect*
feeding it just changed, regardless of magnitude - a brand new command is never eased in behind
a previous one's leftover release tail, even if its own peak happens to be lower than what that
tail was still coasting down from.

### Physical motor mapping

The game's L2/R2 axis identity follows `SwapTriggers`, same as regular input - if swapped, the
physical left trigger reports to the game as its right axis. The effect driving the physical
left trigger's motor is therefore whichever effect the game currently associates with whatever
axis the physical left trigger maps to, evaluated at the physical left trigger's own raw pull
position (not the swapped/output position) - see `PanguEngine.ApplyRumbleOutput`'s
`leftPhysicalEffect`/`rightPhysicalEffect` selection.

### Settings

- `AppSettings.AdaptiveTriggerSimulation` ("Simulate Adaptive Triggers with Trigger vibration",
  Options tab, default off). When on, it claims the trigger motors outright - RumbleMode's own
  trigger routing (`TriggerOnly`, `GripAndTrigger`, etc.) is bypassed for the trigger channel
  specifically. Grip motors are unaffected and keep following RumbleMode, so a user can have
  generic grip rumble and trigger-effect buzz at the same time.
- `AppSettings.AdaptiveTriggerIgnoreIntensity` ("Ignore Rumble Intensity Sliders Above", default
  on). Lets adaptive-trigger-simulated levels skip the `TriggerLeftIntensity`/
  `TriggerRightIntensity` caps and reach full strength (4) regardless of what those sliders are
  set to - for a low ceiling on generic trigger rumble with full-strength adaptive-trigger buzz.
  Only has an effect while Adaptive Trigger Simulation is on. Defaults on so turning Adaptive
  Trigger Simulation on gives the best experience out of the box.
- `AppSettings.AdaptiveTriggerDisableMatchingGrip` ("Disable Matching Grip Motor During
  Adaptive Trigger", default on). Workaround for a device-level limitation: a side's grip
  motor running at the same time as that same side's Adaptive Trigger buzz cuts the trigger
  buzz out, even though the bytes sent to the device never waver - most likely the same side's
  grip and trigger motors share a driver circuit that can't reliably sustain both
  independently. Cross-side combinations (e.g. left trigger + right grip) are unaffected. When
  this setting is on, a side's grip motor is forced to 0 whenever that side's trigger position
  is within its Adaptive Trigger effect's active zone - gated via
  `TriggerEffectSample.ZoneContainsSweep` (position only), not the effect's instantaneous
  `ForceAt` result. That distinction matters for the self-oscillating modes (Vibration/Machine/
  Galloping): gating on the instantaneous force meant a sine dip to zero (happening several
  times a second by design, not a bug) briefly re-enabled grip mid-buzz, re-triggering the same
  hardware conflict this setting exists to prevent and producing an audible/tactile hiccup
  during otherwise-continuous automatic fire (confirmed via live capture). Gating on zone
  presence instead keeps grip suppressed continuously through a self-oscillating effect's whole
  hold. The opposite side's grip is unaffected, and RumbleMode governs grip normally everywhere
  Adaptive Trigger Simulation isn't currently claiming a side's trigger zone. Defaults on for
  the same reason as above.
- `AppSettings.AdaptiveTriggerIncludeGainLevels` ("Include Trigger Gain Intensity Levels",
  Advanced accordion, default off). Self-oscillating effects (Vibration/Machine/Galloping, per
  `TriggerEffectSample.IsSelfOscillating`) are scaled to an 8-tier felt-magnitude scale instead
  of the plain 0-4 one: 0, 1, 2, 3, 4, 2(Gain), 3(Gain), 4(Gain) -
  `PanguEngine.ScaleToTriggerLevelWithGain`. Tiers 5-7 reuse Trigger Gain Mode's bit
  (`HidReader`'s `TriggerGainBit`) as three extra tiers above what level 4 alone can reach,
  toggled per gate-loop tick via `HidReader.TrySendMotors`'s `gainOverride` parameter -
  confirmed via live testing to ride along on the config packet that's already resent every
  tick during buzzing, so this adds no extra HID writes and doesn't touch the user's own
  persisted Trigger Gain Mode setting/checkbox. The gain bit is a single value shared by both
  physical triggers, not independently settable per side - if both are self-oscillating and
  want different gain states at the same instant, they're OR-combined (whichever side wanted
  gain off ends up buzzing louder than it asked for on that tick). Static effects
  (Off/Feedback/Weapon/Bow) are unaffected regardless of this setting. Off by default since
  it's an approximation on top of an approximation - the game's own authored amplitude gets
  remapped onto a scale it was never designed for.
- `AppSettings.AdaptiveTriggerTimingOffsetPercent` ("Adaptive Trigger Timing Offset", Advanced
  accordion, -20 to 20, default 5%). The Pangu has no physical resistance to slow the trigger
  down the way a real DualSense would - a player who'd have felt real resistance building
  through a zone instead sails straight through it, so the buzz can fire noticeably before the
  in-game action it's meant to represent. Shifts the position `ForceAt` is evaluated at (in
  percent of the 0-255 range) to compensate: positive delays the buzz further into the pull,
  negative brings it earlier. Applied uniformly ahead of every mode via
  `PanguEngine.ApplyTimingOffset`, before `ForceAt` ever sees the position.
- `AppSettings.AdaptiveTriggerReleaseMs` ("Adaptive Trigger Release Time", Advanced accordion,
  0-300ms, default 40ms). Only applies to the static modes (Off/Feedback/Weapon/Bow) - a fast
  single-shot pull-and-release can carry the trigger through one of those effects' force zones
  in less time than one gate-loop tick, producing barely a felt blip; easing the release instead
  of snapping straight to 0 gives it a felt tail. Bypassed entirely for
  `TriggerEffectSample.IsSelfOscillating` effects (Vibration/Machine/Galloping) - they already
  vary over time on their own for as long as they're held (real-world frequencies seen/
  referenced - 15-33Hz - land well inside typical release-time windows), so smoothing on top
  would mostly just blur their own oscillation rather than help. See
  `PanguEngine.ApplyAdaptiveTriggerRelease`.

### Gate loop sampling rate

`PanguEngine.RumbleGateLoop` ticks at a dynamic rate rather than a fixed one: ~3ms (via the same
high-resolution waitable timer `HidMaestroOutput.SubmitThreadProc` uses for submitting input
state, see `NativeTiming`) whenever a self-oscillating effect is active on either side, falling
back to the normal ~16ms otherwise. Confirmed via live capture that a 12Hz effect sampled at a
fixed ~16ms (with real-world OS scheduling jitter on top) only got ~3-4 samples per cycle
landing at effectively random phase points - the underlying sine is mathematically clean the
whole time, but that sparse/uneven sampling made the felt rhythm read as jittery/inconsistent
rather than smooth. The tighter tick fixed this in testing.

## Audio Auto Haptics

Derives an *approximate* rumble signal from whatever audio the PC is currently playing (bass +
transients) for games that send no rumble of their own. This is a heuristic over speaker/
headphone output, not literal per-game haptic-feedback content - no virtual controller can
receive that at all, real DualSense audio-haptics or otherwise; see "Audio-based haptic
feedback (not supported)" below for why. `AudioAutoHapticsCapture` does the capture and DSP;
`PanguEngine.ApplyRumbleOutput` decides how the result blends into each motor group. Off by
default (`AppSettings.AudioAutoHapticsEnabled`).

### Capture and DSP chain

WASAPI loopback capture (`NAudio.Wave.WasapiLoopbackCapture`) against a chosen render device,
processed per audio buffer in `AudioAutoHapticsCapture.OnDataAvailable`:

1. **Channel classification and fold-down** (`AudioAutoHapticsCapture.ClassifyChannels`) - every
   channel the format's WAVEFORMATEXTENSIBLE speaker mask designates as a left-side position
   (front, back, side, or height - doesn't matter which) sums directly into the Left chain, and
   the mirrored right-side positions sum into the Right chain, with no averaging between them.
   So on a 5.1/7.1 source, content on the *rear* left channel still buzzes the left motor, not
   just the front-left pair. Center-ish positions (front/back/top center) and the LFE/subwoofer
   channel are non-directional by nature, so each folds into *both* chains equally instead,
   independently controlled: `AudioAutoHapticsIncludeCenter` and `AudioAutoHapticsIncludeLfe`
   (both default on) - turning either off skips just that channel, not the whole fold-down. A
   channel the mask doesn't recognize, or a >2-channel format with no mask to classify by at
   all, falls back to folding into both chains equally rather than being silently dropped. Plain
   stereo (or no mask) assumes conventional Left/Right channel order.
2. **Low-pass filter** (`CutoffHz`, default 160 Hz) - isolates bass, since that's what a rumble
   motor can actually reproduce the feel of.
3. **Asymmetric envelope follower** (`AttackMs`/`ReleaseMs`, default 1.0 ms / 80 ms) - tracks the
   filtered signal's magnitude, ramping up fast on transients and fading slower after, like a
   compressor's envelope.
4. **Intensity boost** (`IntensityBoost`, default 3.0x) - the envelope modulates the filtered
   signal's depth, emphasizing loud moments over quiet ones.
5. **Soft clip** (`x / (1 + |x|)`) - bounds the result to `(-1, 1)` regardless of how hot the
   boosted signal gets, avoiding a hard-clip harshness.
6. The per-buffer peak of the soft-clipped signal becomes `LeftIntensity`/`RightIntensity`
   (0-255), read continuously by `PanguEngine`'s rumble gate loop. No noise gate is applied at
   this stage - see Noise Floors below for why.

This DSP chain (low-pass, envelope follower, soft clip) is a ported algorithm from
loteran/DS5Dongle's "Audio Auto Haptics" feature (MIT licensed) - see
`THIRD-PARTY-NOTICES.md`. PanguBridge's implementation is an independent C# port targeting
NAudio/WASAPI, not a copy of DS5Dongle's own source.

### Device selection

`AppSettings.AudioAutoHapticsDeviceId` selects what to capture: null/empty tracks the current
Windows **Default Device** (Multimedia role), the literal string `"communications"` tracks the
**Default Communication Device** (Communications role), and anything else pins a specific
device by its MMDevice ID regardless of what Windows currently considers default. The two
"tracking" modes follow live default-device changes via
`IMMNotificationClient.OnDefaultDeviceChanged` - device-added/removed notifications are
deliberately not hooked; the Options UI's device list only refreshes via its manual refresh
button.

### Applying the signal to motors (`PanguEngine.ApplyRumbleOutput`)

Audio Auto Haptics runs after RumbleMode routing, so it only ever touches a motor group
RumbleMode has already made active - it can never turn on a motor RumbleMode currently
excludes - and before Adaptive Trigger Simulation, so ATS still wins outright for the trigger
group regardless of these settings.

Each active motor group's mode (`AudioAutoHapticsMode`: **No Audio Rumble**, **Replace with
Audio Rumble**, or **Normal + Audio (Normalized)**, the last one clamping the sum instead of
overflowing) decides how audio blends with whatever RumbleMode/Steam already had that motor
doing:

- **Grip** uses one mode (`AudioAutoHapticsGripMode`, default No Audio Rumble) - grip has no
  press/pull state to key off of.
- **Trigger** uses one of two modes depending on that side's *live* trigger pull position:
  `AudioAutoHapticsTriggerPulledMode` (trigger > 0, default Normal + Audio (Normalized)) or
  `AudioAutoHapticsTriggerIdleMode` (trigger == 0, default Replace with Audio Rumble). This
  lets a single Rumble Type choice (e.g. `GripAndTrigger`, always active) produce blended
  rumble while a trigger is pulled and audio-only buzz while it's idle, without needing a
  RumbleMode variant for every combination.

### Noise Floors

Ambient/background audio can otherwise keep a motor lightly buzzing even at very low volume.
Three independent floors (percent of the soft-clipped 0-255 range, `Math.Clamp`-ed to 0-100)
gate a sample to 0 instead of letting a small nonzero value through:

- `AudioAutoHapticsGripNoiseFloor` (default 35%)
- `AudioAutoHapticsTriggerPulledNoiseFloor` (default 10%)
- `AudioAutoHapticsTriggerIdleNoiseFloor` (default 35%)

Gating happens in `PanguEngine.ApplyRumbleOutput` (`ApplyNoiseFloor`), not in
`AudioAutoHapticsCapture` itself, since the same captured Left/Right intensity feeds all three
motor groups and each needs a different threshold - gating once upstream, before it's known
which group a sample is about to drive, couldn't apply a per-group floor.

## Audio-based haptic feedback (not supported)

The real DualSense has a third feedback channel beyond grip rumble and adaptive triggers:
audio-based haptic feedback, delivered over channels 3 and 4 of the controller's quad-channel
USB audio device rather than the HID output report. Games use this for finer, more
context-dependent texture (footsteps, weapon-specific feel, ambient rumble) than the legacy
rumble bytes carry. It requires the controller's audio device to be exposed to the PC and does
not work over Bluetooth even on real hardware.

### HIDMaestro's output report has no field for it

HIDMaestro's `dualsense-edge` profile (`profiles/sony/dualsense-edge.json` in its repo) declares
every field of the 64-byte `extendedOutputReport` explicitly: `validFlag0/1/2`,
`leftMotor`/`rightMotor`, `headphoneVolume`/`speakerVolume`/`micVolume`/`audioControlFlags`,
`muteLed`, `leftTriggerEffect`/`rightTriggerEffect`, `lightbarSetup`/`ledBrightness`/
`playerIndicator`/`lightbar`, and a raw `effectPayload` catch-all covering the same bytes already
named. Nothing in that list carries haptic-feedback audio data - the `headphoneVolume`/
`speakerVolume`/`micVolume`/`audioControlFlags` bytes are volume-level controls for the real
DualSense's own onboard speaker/headphone jack/mic (an unrelated, minor hardware feature), not a
haptic waveform channel. `HidMaestroOutput.OnOutputDecoded` only reads `leftMotor`, `rightMotor`,
`validFlag0`, `leftTriggerEffect`, `rightTriggerEffect`, `validFlag1`, and `lightbar` - the full
set of fields that carry anything relevant.

No secondary device gets created for it either. HIDMaestro's own wiki ("SwDevice and PnP") lists
exactly what a virtual controller creates: an HID device plus, for XInput-style profiles, an XUSB
companion device - no audio endpoint. "Cross-API Coverage" independently lists only DirectInput,
XInput, SDL3, the browser Gamepad API, and WGI/GameInput as covered; no audio API appears
anywhere. The real DualSense's haptic-feedback channel lives on a completely separate USB
interface (Audio Class) that HIDMaestro never implements, so the data has nowhere to land -
this isn't a field PanguBridge is failing to read, it never reaches this pipeline at all.

### Confirmed against PadForge - even real-hardware bridging can't reach it

[PadForge](https://github.com/hifihedgehog/PadForge) is the most capable known HIDMaestro
consumer for Sony-pad feature parity, and it does have Sony-specific audio code
(`AudioPassthroughService.cs`, `DualSensePassthroughDispatcher.cs`) - checked directly against
its source to see whether it had found a way around this. It hasn't, and its own approach
explains why the gap can't be closed even with real hardware in the loop:

- `AudioPassthroughService.cs` locates a target pad's USB Audio Class endpoint by **Container-ID
  match** - matching the HID interface and the audio interface that share a container on *the
  same physical pad*. This requires genuine Sony hardware to exist as a real Windows audio
  device in the first place; a virtual HIDMaestro controller has no real USB Audio Class
  interface and no container ID for anything to match against. The technique is structurally
  inapplicable to an emulated controller, independent of how it's implemented.
- Even when writing to a real DualSense's real audio device, PadForge's own comments describe
  the payload as "channel 1 carries the mono program mix (the firmware's speaker tap), remaining
  channels (DualSense haptic actuators) zeroed" - i.e. it plays system audio/macro sounds through
  the controller's speaker, and deliberately leaves the haptic-actuator channels silent. It does
  not capture or forward a game's own haptic-feedback stream, even where it has a real device to
  write to.
- `DualSensePassthroughDispatcher.cs` forwards the standard output report (rumble/trigger
  effects/lightbar - the same fields already covered above) to an assigned physical DualSense via
  `SDL_SendGamepadEffect`, for bridging virtual input to real hardware output. Same requirement:
  a real Sony pad has to be the destination.

Both of PadForge's audio-adjacent features exist specifically because a real DualSense is
present to target. Neither offers a path for a purely virtual controller, which is PanguBridge's
situation - there is no code path, in HIDMaestro or in its most capable known consumer, that
delivers game-authored haptic-feedback content to an emulated controller with no real Sony
hardware behind it.

### Hardware ceiling, if it were ever reachable

Even with a virtual audio device, the Pangu's grip/trigger motors are simple ERM-style buzz
motors, not the DualSense's voice-coil HD haptic actuators - decoded haptic-feedback audio could
only ever be approximated as buzz intensity/timing, the same ceiling Adaptive Trigger Simulation
already has for trigger resistance, not reproduced as true haptic texture.
