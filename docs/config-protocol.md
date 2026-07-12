# On-Controller Config Protocol (Profile Data)

The Pangu persists a full configuration blob on its own flash - button remaps, stick curves,
LED/rumble settings, and the motion-sensing (gyro) mapping. iControl reads and writes it over
HID Interface 4 with a small command family that PanguBridge has been partially using all
along without knowing it: the `0x62` "configure" packet documented in `docs/rumble.md` and
`docs/led.md` is actually this protocol's *write base-config block* command.

Everything here was reverse-engineered from iControl's own JS protocol layer (`pgv1.js` inside
`resources/app/00000000.asar`, plus `profile_helper.js` for the bit-packing), then confirmed
live against the controller (reads, writes, and read-back verification).

## Command framing

All commands ride output report ID `0x02` on Interface 4. Byte[1] packs three fields:

```
byte[1] = cmd | (subcmd << 4) | (type << 6)
```

Known combinations (all confirmed live except where noted):

| byte[1] | Meaning | Notes |
|---------|---------|-------|
| `0x52` | `getProfileData(type=1)` - read base-config block(s) | Reply: report `0x52`, same framing, payload after byte[3] |
| `0x62` | `setProfileData(type=1)` - write base-config block(s) | The known "configure" packet; device ACKs with a short `0x62` reply on the IN pipe |
| `0x92` | `getProfileData(type=2)` - read global config (16 bytes: game_mode/game_type/theme_id) | No response when probed - and a full capture of iControl's own session shows it **never sends this command** on this device generation, so it isn't a real gap |
| `0xD2` | `getProfileData(type=3)` - read macro buffer (192 bytes/slot) | Works; slot 0 read back all-zero (no macros configured) |
| `0x12` | `getExtProfileData` / `getFlashProfile` - per-socket area configs | Request the full 192 bytes in ONE command (`02 12 [socket] 00 30`, socket 0-3) and the device streams 4 auto-chunked replies (per-chunk `adr>0` requests get nothing - the earlier probe's mistake). Reply layout: `02 12 [socket] [moduleType] [adrBlocks] [lenenc] payload...` - byte[3] is the **installed module's hardware type id** (this unit: sockets 0,3 = 5 (stick), 1 = 1 (d-pad), 2 = 2 (face buttons)). Content on this unit is factory-default per the 192-byte area struct |
| `0x15` | `getBaseInfo` - status/info struct | iControl polls this every ~500ms; response is static per-session |
| `0x25` | `getKeyEvent` - poll input state | This is what PanguBridge's keepalive has been sending |
| `0x35` | `getTestData` - factory gyro/accel/button report | No response over the wireless dongle (see below) |
| `0x17` | `enterTestMode` (`02 17 01`) | No response over the wireless dongle |
| `0x13` | Commit/apply pulse | Sent after `0x62` writes; no payload |
| `0x37` | Enable/handshake | Reply (`02 37 CE 5F 16 DA E4 80`) is a MAC-format ID sharing its last 3 bytes with getMac's reply - the paired radio link's identity |
| `0x55` | `getFWInfo` | Reply `02 55 01 01 04 01 ...` - firmware version bytes |
| `0x65` | `getFWInfo_MLMR` (`02 65 02`) | Reply `02 65 02 01 00 07 ...` - secondary/module firmware version |
| `0x27` | `getMac` | Reply `02 27` + 6-byte MAC (`DC:AA:15:DA:E4:80` on this unit) |
| `0x11` | `syncMode` (type 1) - host sets a UI/sync mode + profile nibble in byte[3] (observed `0x14`, `0x54`) | The device echoes the current value back in every `0x21` heartbeat's byte[3]; this is the old `02 11 00 14` "status-query" from `docs/hid-report-format.md`, and the brief `[12..15]` blip after it is the processed input block recomputing on the mode change |
| `0x31` | `syncMode` (type 3) - self-check screen poll | Empty echo reply; polled at ~10 Hz while iControl's Self-Check tab is open |

Additional validation from a full USBPcap capture of iControl's own session (`FullRun.pcapng`,
2026-07-10): toggling the Somatosensory gyro mode in iControl's UI produced exactly the
`02 62 03 0C` block-48 read-modify-write + `0x13` commit sequence this document describes, with
only `feeling_map_type` changing (`0x81`↔`0x83` at blob byte 69) - byte-identical to what the
probing tools send. Pressing the physical Profile button produced **no traffic at all** on
Interface 4 (with all profile slots at factory default it is either local-only or a no-op),
so profile switches cannot currently be observed by the host this way.

Block addressing for `getProfileData`/`setProfileData` (`adr`/`len` in bytes, converted to
16-byte units on the wire):

```
byte[2] = (adr/16) & 0xFF
byte[3] = ((adr/16) >> 8 & 0x3) | ((len/16) << 2)
payload (writes) / reply payload (reads) = the block bytes, starting at byte[4]
```

iControl reads/writes in 48-byte blocks. Writes only take effect after the `0x13` commit, and
the value then persists on the controller across power cycles.

## The base-config blob (type 1)

624 bytes total. The full bit-level layout comes from iControl's `appset_baseData_o` struct
(fields pack sequentially, LSB-first within each byte, no padding - see `profile_helper.js`'s
`convProfilePos`). Selected confirmed fields:

| Offset (byte.bit, width) | Field | Meaning |
|--------------------------|-------|---------|
| 0.0 w32 | `activateFlag` | Must be `0xC3A5963C` (this is `docs/led.md`'s "preamble" bytes `3C 96 A5 C3`) |
| 4.0 w8 | `light_type` | 0=breathe 1=solid 2=flow |
| 5.0 w24 | `light_rgb` | LED color (B,G,R byte order on the wire) |
| 8.0 w1 | `shake_flick` | Flash lights on vibrate (`docs/led.md` byte[12] bit 0x01) |
| 8.5 w1 | `key_motorCtrl` | **Trigger Gain Mode** (`docs/led.md` byte[12] bit 0x20) - iControl's struct comment says "BACK/START vibration feedback" but its UI labels this Gain Mode |
| 8.6 w1 | `light_off` | Front/logo light off (bit 0x40) |
| 8.7 w1 | `light_off_bar` | Grip lights off (bit 0x80) |
| 9.0 w4 | `light_intensity` | Brightness 0-4 (`docs/led.md` byte[13] low nibble) |
| 9.4 w4 | `shake_Ltrigger_intensity` | Left trigger buzz level 0-4 (byte[13] high nibble) |
| 10.0/10.4/11.0 w4 | grip/trigger intensities | Match `docs/rumble.md`'s levels |
| 13.0/14.0 w8 | `shake_L/Rtrigger_value` | Trigger-travel vibration thresholds |
| 17/29 +12 each | `config_stick_index0[0..1]` | Per-stick: mapping target, X/Y invert, deadzone, curves, `sample_type` (0=overclock 1=12bit) |
| 69.0 w2 | **`feeling_map_type`** | Gyro mapping: 0=none **1=stick** 2=buttons 3=mouse |
| 69.5 w3 | `feeling_mapStick_sensitivity` | 0-4 |
| 70.3 w1 | `feeling_mapStick_select` | 0=left stick, 1=right stick |
| 70.4 w2 | `open_feelingMap_manner` | 0=always on, 1=hold button, 2=toggle button |
| 70.6 w6 | `open_feelingMap_Button` | Activation button keycode |
| 71.4 w10 | `feeling_compensate` | Compensates the game's own stick deadzone |
| 73.0-73.3 w1 each | feeling stick X/Y inverts | Per-axis gyro-to-stick inversion |
| 75.4 w4 | `mouse_move_configuration` | See negative result below |
| 105-108 | calibration flags | `enter_3D_Cal`, `CAL_3D_sucess`, trigger-cal equivalents |

(The full 190-field table can be regenerated by replaying `convProfilePos` over
`appset_baseData_o`; only load-bearing fields are listed here.)

## Gyro: what works and what doesn't

**The IMU is functional.** Gyro-as-mouse moves the Windows cursor (endpoint 0x83, gated by the
physical toggle), and Button-mode gestures fire bits in the processed button bytes
(`docs/usb-reverse-engineering.md`). The factory-test screen's flat-zero gyro readout and the
dead `0x35`/`0x17` test commands are a broken/ungated diagnostic path *over the wireless
dongle* (iControl's own code has a `test_mode_usedongle` flag that skips test-mode init on
dongle connections) - not a dead sensor. Earlier conclusions that the IMU might be unpopulated
are wrong.

**Confirmed working - fused gyro-to-stick on Interface 4.** Writing `feeling_map_type=1` (with
`feeling_mapStick_select` choosing the stick) makes the firmware mix gyro motion into the
*processed* stick bytes `[12..15]` of the normal `0x25` input report (see
`docs/hid-report-format.md`): smooth full-range analog at report rate, blended additively with
real stick input, while raw `[2..5]` stays raw. Verified live in both directions (tilt-only
moved `[14]/[15]` with `[4]/[5]` still; stick-only moved both nearly 1:1). This is a complete
on-controller gyro-aiming pipeline - a future PanguBridge feature only needs to write this
field and read the processed bytes for the affected stick.

**Negative result - no raw gyro on Interface 4.** `mouse_move_configuration=10` (whose struct
comment promises "id5's Lx,Ly transmit the mouse values") produced no observable change in any
byte of any report on Interface 4, in mouse mode, with the cursor actively moving from gyro
tilt - so the raw angular-rate data (the gyro-mouse deltas) is only available on the dedicated
mouse endpoint 0x83. If raw rate data is ever wanted, the plausible route is reading the
Pangu's HID mouse interface via Raw Input (optionally with HidHide so the deltas stop moving
the real cursor), not this config field.

## Other blob types (dumped live)

- **Global config (type 2)**: 4 meaningful bytes (`game_mode`, `game_type`, `theme_id`) per
  iControl's struct, but the read command gets no reply over the dongle - likely another
  dongle-gated path like the test commands.
- **Macro buffer (type 3)**: 192 bytes per macro slot (`area_id`, `area_type`, 32 steps of
  keycode + hold/next timing, per iControl's `appset_macrobuf_o`). Readable; empty on this
  unit.
- **Area/profile configs (0x12)**: 192-byte struct (`appset_function_area_o`: activateFlag,
  area_type 0=stick/1=buttons/2=dpad, one stick config with three sensitivity-curve variants,
  7 key mappings), one per module socket (0-3), read whole in one request - see the command
  table above for the framing and the per-socket module-type byte. The base-config chunk at
  offset 384 never answers even to iControl itself - unresponsive flash regions simply stay
  silent rather than erroring.
- **`0x25` report bytes [33..34]**: still unidentified (tracks RX/RY; beyond iControl's parsed
  structs) - see `docs/hid-report-format.md`.

## Cautions

- `setProfileData` writes persist on the controller and are shared state with iControl - the
  same rule as everywhere else applies: don't run both apps at once, and preserve unrelated
  bits (read-modify-write the block, never build it from zero). PanguBridge's own
  `SendConfigurePacket` writes block 0 from a zeroed template - safe today only because the
  fields beyond its known bytes happen to be zero on this unit's block 0.
- The factory-test screen (`docs/usb-reverse-engineering.md`) and the `0x35` test commands are
  dongle-gated; nothing here enables them.
