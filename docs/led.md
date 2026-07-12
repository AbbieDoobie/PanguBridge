# LED / RGB Support

The DualSense Edge output report includes lightbar color/brightness data (HIDMaestro's
`dualsense-edge` profile exposes this via `extendedOutputReport`: `lightbar` is an `rgb24`
field at bytes 45-47, `ledBrightness` at byte 43, `playerIndicator` at byte 44, `lightbarSetup`
at byte 42). PanguBridge reads only the `lightbar` field and drives the physical Pangu's LED to
match - Color, Saturation, and Brightness all work through Steam Input since Steam folds all
three into the RGB value it sends. Per-zone on/off and Flash-on-Vibrate are not yet forwarded
from games - see "Not yet implemented" below.

The Pangu's native LED protocol was reverse-engineered via USBPcap capture of iControl's own
output traffic (LED color is set by iControl writing output reports to the device, not
something visible in the device's input reports, so this required watching iControl's traffic
directly rather than using `tools/HidCapture`, which needs exclusive device access that
conflicts with iControl - see CLAUDE.md's "Top-Level Gotchas").

## Protocol

LED state is set via the same `0x62` "configure" report documented in `docs/rumble.md` for
trigger resistance levels - it is not a separate command. Every `0x62` write carries a combined
snapshot of trigger level and LED state together, whether or not both are changing. A
no-payload `0x13` report (`02 13 00 00...`) follows immediately after each `0x62` write, acting
as a commit/pulse - the same pattern used by the mode-toggle follow-ups documented in
`docs/usb-reverse-engineering.md`.

### Byte layout (0-indexed from byte[0] = report ID)

```
02 62 00 0C 3C 96 A5 C3 01  [B] [G] [R] [Zone] [TrigL<<4|Bright] [TrigR*0x10+4] 04 00 FF FF 00...
idx: 0  1  2  3  4  5  6  7  8    9   10  11    12         13                14        15 16 17 18
```

- **[2-8]**: fixed 7-byte preamble, constant across every observed write (`00 0C 3C 96 A5 C3
  01`); meaning unconfirmed.
- **[9]/[10]/[11]**: LED color as **Blue, Green, Red**, in that byte order. E.g. blue selected
  in iControl encodes as `5D 11 00` (B=0x5D, G=0x11, R=0x00); red selected encodes as `00 00
  FF` (pure red).
- **[12]**: LED zone/flash bitmask - independent bits that OR together:
  - `0x80` = grip lights (both, as a single combined zone - no separate left/right grip bit
    exists anywhere in the packet; the full byte range is otherwise accounted for, so there is
    no room for a hidden per-grip bit in this command. This does not rule out the hardware
    itself being capable of finer control - only that this protocol, as exercised, does not
    expose it)
  - `0x40` = front light disabled
  - `0xC0` = both disabled (`0x80 | 0x40`)
  - `0x20` = Trigger Gain Mode enabled (iControl's "Enable Trigger Gain Mode" toggle - increases
    trigger vibration intensity at all levels; unrelated to LED despite sharing this byte).
    Confirmed via a controlled USBPcap capture: toggling the setting 7 times in iControl produced
    7 alternating `0x62` writes with only this bit differing, and separately confirmed readable
    back via `BaseProfileQuery` below (not just an artifact of the write). See
    `docs/usb-reverse-engineering.md`.
  - `0x01` = Flash Lights on Vibrate enabled, ORed on top of whatever zone bits are already set
- **[13]**: `(LeftTriggerLevel << 4) | BrightnessLevel` - nibble-packed. Brightness is a
  0-indexed 0-4 scale (iControl's UI brightness buttons 4→1 encode as `03,02,01,00`). Left
  trigger level occupies the high nibble, matching `docs/rumble.md`'s trigger-config byte
  (`LeftTriggerLevel * 0x10`) when brightness's low nibble is 0. Not independently confirmed
  with both trigger level and brightness nonzero at the same time.
- **[14]**: `RightTriggerLevel * 0x10 + 4` - matches `docs/rumble.md` exactly.
- **[15-18]**: `04 00 FF FF` - constant, matches the trigger-config bytes.
- **[19+]**: `00` padding.

### Reading byte[12] back (`getProfileData` / report `0x52`)

Unlike every other field in this report, byte[12] (specifically Trigger Gain Mode) is not
write-only. iControl's own protocol layer (`pgv1.js`) has a `getProfileData(type, adr, len)`
request that reads the controller's persisted base-config blob directly from flash:

```
Request:  02 52 00 0C 00 00...   (getProfileData(type=1, adr=0, len=48))
Response: 02 52 00 0C 3C 96 A5 C3 01 [B] [G] [R] [Zone] [TrigL<<4|Bright] [TrigR*0x10+4] 04 00 FF FF 00...
```

The response is report ID `0x52` (not `0x62`) but is otherwise byte-for-byte the same layout as
the `0x62` write above, confirmed by the fixed preamble (`00 0C 3C 96 A5 C3 01`) landing at the
identical offset. This was verified with a listen-only probe that sent only the enable handshake
and this query - no `0x62` write at all that session - and still got back the controller's real,
previously-set Trigger Gain state, ruling out this being an echo of something PanguBridge itself
wrote. This is the only confirmed read path for any bit in this report; the `0x62` write's own
immediate IN-pipe ACK (a distinct, shorter reply) only fires right after a write and does not
reflect state otherwise, and iControl's periodic `getBaseInfo` status poll (report `0x15`) queries
an unrelated struct and never reflects this bit either.

## Implementation

- `HidReader.ConfigPreamble` covers only the genuinely-fixed 7 bytes (`[2]-[8]`).
- `HidReader` caches LED state (`_ledRed`/`_ledGreen`/`_ledBlue`, `_ledZoneFlags`,
  `_ledBrightness`), defaulting to white/max-brightness/all-zones-on/flash-off (the Pangu's
  factory-default state). `SendConfigurePacket` builds every `0x62` write from that cache plus
  whatever trigger level is current, so a trigger-only send never overwrites LED state and an
  LED-only send (`TrySetLedColor`) never disturbs an in-progress trigger buzz. The device has no
  concept of a partial update - every `0x62` write must carry the full combined state.
- `HidReader.QueryTriggerGainMode` sends `BaseProfileQuery` and blocks (synchronously, called
  from `RunSessionAsync` before the main read loop starts and before `_activeStream` is
  published) for the `0x52` reply, seeding `_ledZoneFlags` with the controller's real byte[12] -
  including any zone/flash bits a user may have set via iControl, not just Trigger Gain Mode -
  instead of this class's own in-memory default. This has to happen before `_activeStream` is
  set: any config-packet write before that point would otherwise carry the still-default
  `_ledZoneFlags` and silently reset the controller's real state. Best-effort - a timeout or
  write failure just leaves `TriggerGainMode`/`_ledZoneFlags` as they already were rather than
  blocking the connection.
- `HidReader.TrySetTriggerGainMode` flips only `TriggerGainBit` (`0x20`) within whatever
  `_ledZoneFlags` currently holds and sends immediately - it's a controller-side setting the
  Pangu itself persists, not an app-level one, so there's no settings.json entry for it.
  `SettingsView` reflects `HidReader.TriggerGainMode` on load and via the
  `TriggerGainModeChanged` event, rather than assuming a value.
- `HidMaestroOutput` decodes the `lightbar` RGB24 field, gated on `validFlag1` bit `0x04`
  (`DS_OUTPUT_VALID_FLAG1_LIGHTBAR_CONTROL_ENABLE`, byte 2 of the report - a different byte from
  the trigger-effect valid flags in byte 1, per the same full-buffer-replace/valid-flag
  reasoning documented in `docs/rumble.md`). Fires `LightbarChanged(r, g, b)`.
- `PanguEngine.OnLightbarChanged` forwards directly to `HidReader.TrySetLedColor` - no gate loop
  needed, since LED color (unlike adaptive-trigger force) doesn't depend on any live physical
  state to re-evaluate.

## Not yet implemented

- The DualSense output report's dedicated `ledBrightness` enum field (byte 43) and
  `lightbarSetup` on/off field (byte 42) are not read by `HidMaestroOutput` at all - only the
  `lightbar` RGB24 field is decoded. Steam Input's own Color/Saturation/Brightness controls
  still work end-to-end because Steam applies all three to the RGB value it sends as `lightbar`
  before PanguBridge ever sees it, rather than using the separate `ledBrightness` field. The
  Pangu's own native wire brightness nibble (`HidReader._ledBrightness`, byte[13]'s low nibble)
  is hardcoded to `4` (max on the Pangu's 0-4 scale) and does not vary with Steam Input's
  Brightness slider - all of the perceived brightness change comes from the RGB scaling
  described above, not from this nibble. Zone on/off and Flash-on-Vibrate remain unforwarded, at
  their all-zones-on/flash-off defaults.
- Zone bits (`0x80` grip off / `0x40` front off) and Flash-on-Vibrate (`0x01`) are not exposed
  as user-facing PanguBridge settings - unlike Trigger Gain Mode (`0x20`), which now is (see
  "Reading byte[12] back" and "Implementation" above), these three remain unread and unwritten,
  though `QueryTriggerGainMode` now preserves whatever value they hold on the controller instead
  of clobbering them back to their all-zones-on/flash-off defaults on the next write.
- The byte[13] nibble-packing formula would benefit from a capture with trigger level and
  brightness both nonzero at once, if brightness control is ever added.
