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

The layout matches `Ohjurot/DualSense-Windows`'s `DS5_Output.cpp` (`processTrigger`), a widely
used open-source reference PC games build against for this feature - this is a
community-reverse-engineered protocol, not an official Sony specification. Byte 0 of each
11-byte blob is a mode ID; the rest are mode-specific parameters. `AdaptiveTriggerEffect.cs`
decodes three modes:

- `0x00` Off - no resistance requested.
- `0x01` Continuous - uniform force (`byte[2]`) starting at `byte[1]` and holding to full pull.
- `0x02` Section - a binary "wall": full force between `byte[1]` (start) and `byte[2]` (end),
  nothing outside that range.
- `0x26` EffectEx - three-zone force curve. `byte[1]` is `0xFF - startPosition` (the encoder
  stores it inverted); `byte[4]`/`byte[5]`/`byte[6]` are begin/middle/end force.
  `TriggerEffectSample.ForceAt` blends between the three zones linearly - the real hardware's
  exact interpolation isn't publicly documented, so this is an approximation on top of the
  Pangu's own hardware limits.
- Anything else (including `0xFC` Calibrate, and any multi-zone "Feedback"/"Weapon" mode this
  decoder doesn't recognize) is treated as Off.

### Live evaluation

A game sends a new trigger-effect packet only when the effect changes, not on every trigger
movement, so the resistance curve has to be re-evaluated continuously against the physical
trigger's current position. `PanguEngine` caches the latest decoded `TriggerEffectSample` per
side and re-evaluates `ForceAt(currentPosition)` on the same ~60 Hz rumble gate loop the
`...IfPulled` RumbleModes use to react to physical trigger pull/release between Steam
packets (`PanguEngine.RumbleGateLoop`), so the buzz tracks the pull in real time.

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
  this setting is on, a side's grip motor is forced to 0 whenever Adaptive Trigger Simulation
  is actively buzzing that same side's trigger; the opposite side's grip is unaffected, and
  RumbleMode governs grip normally everywhere Adaptive Trigger Simulation isn't currently
  claiming a side's trigger. Defaults on for the same reason as above.

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
