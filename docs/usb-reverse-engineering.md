# USB Reverse Engineering - Beitong Pangu BTP-PG01A

All captures performed with Wireshark + USBPcap on Windows.

---

## Device Identification

```
VID:            0x20BC
PID:            0x5162
Product string: BTP-PG01A XINPUT DONGLE
bNumInterfaces: 5
bMaxPower:      500mA
```

---

## Interface Map

### Interface 0 - XInput
```
bInterfaceClass:    Vendor Specific (0xFF)
bInterfaceSubClass: 0x5D
bInterfaceProtocol: 0x01
Endpoint IN:        0x81 (32 bytes, 1ms interval)
Endpoint OUT:       0x01 (32 bytes, 8ms interval)
```
Standard XInput. Windows XInput driver claims this. Reports 20-byte XInput state at ~1000Hz.
PanguBridge does not read this interface - see Interface 4.

### Interface 1 - XInput Security
```
bInterfaceClass:    Vendor Specific (0xFF)
bInterfaceSubClass: 0xFD
bInterfaceProtocol: 0x13
Endpoints:          none
```
No data flows here.

### Interface 2 - Keyboard HID
```
bInterfaceClass:    HID (0x03)
bInterfaceSubClass: Boot Interface (0x01)
bInterfaceProtocol: Keyboard (0x01)
Endpoint IN:        0x82 (64 bytes, 2ms interval)
```
Used by iControl to output extra button presses as keystrokes. Only active when iControl is
running and buttons are configured to send keys.

### Interface 3 - Mouse HID
```
bInterfaceClass:    HID (0x03)
bInterfaceSubClass: Boot Interface (0x01)
bInterfaceProtocol: Mouse (0x02)
Endpoint IN:        0x83 (64 bytes, 1ms interval)
```
Used by iControl to output processed gyro as mouse movement. Raw gyro data is not exposed here
- the firmware processes it internally before emitting mouse deltas. See "Gyro" below.

### Interface 4 - Vendor HID (the one PanguBridge uses)
```
bInterfaceClass:    HID (0x03)
bInterfaceSubClass: No Subclass (0x00)
bInterfaceProtocol: 0x00
Usage Page:         0xFF80 (vendor defined)
Endpoint IN:        0x84 (64 bytes, 4ms interval)
Endpoint OUT:       0x02 (64 bytes, 4ms interval)
HID Report length:  32 bytes
```

---

## Interface 4 Activation

Interface 4 does not stream data until the host opens the HID device handle and sends an enable
command on the OUT endpoint. iControl does this on startup; PanguBridge replicates it. Without
sending it, `Read()` blocks forever.

### Enable Command
Send once on startup via the OUT endpoint (0x02):
```
02 37 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```
(64 bytes: report ID 0x02, command byte 0x37, then zeros)

### Keepalive
Send every 250ms, alternating between these two:
```
02 25 00 00 ... (normal keepalive)
02 21 00 00 ... (alternate keepalive)
```

---

## Gyro

Raw gyro/IMU data is not accessible anywhere in this device's USB traffic, on any endpoint,
under any command sequence tested, in either PC or Nintendo Switch mode. Gyro is out of scope
for PanguBridge. Details below.

**Endpoint 0x84 (vendor HID, what PanguBridge reads) is dead for gyro.** Every `0x25`/`0x21`
report is byte-for-byte identical whether the controller is at rest, idle with gyro on, or
actively rotating. Bytes 2-5 (sticks) and the rest of the report never move with controller
motion or gyro toggle state.

**Endpoint 0x83 (mouse) carries only processed cursor deltas, not raw sensor data.** Active
whenever the boot-mouse interface is open (iControl, or possibly Windows' own generic HID mouse
driver) and the gyro toggle is on. Report structure is a 7-byte extended mouse report (Report
ID, Buttons, X int16 LE, Y int16 LE, Wheel). Across 4277 packets during active rotation, only 9
distinct payloads appeared, with X deltas roughly -6 to 0 - small, heavily quantized
cursor-speed output.

**A one-off blip near the enable command is not gyro-related.** iControl periodically sends `02
11 00 14` (a status-query command PanguBridge never sends), and for 1-2 report cycles after it,
bytes `[14]`/`[15]`/`[33]`/`[34]` of the next `0x25` report briefly shift away from their `0x80`
idle value before reverting. Sending `02 11 00 14` on its own (via `tools/HidCapture`) at a
1-second cadence while moving the controller in all directions shows no correlation between the
blip's magnitude and actual motion - every burst is essentially identical regardless of motion,
matching the query's own timer. The blip is some other fixed artifact of that status query
(self-test echo, sequence counter, or similar), not motion data. This also means
`docs/hid-report-format.md`'s claim that bytes 12-15/33-34 are "redundant mirror copies of the
same stick/button data" does not hold for bytes 14-15/33-34 specifically - the primary stick
bytes (2-5) stay exactly at rest throughout every blip, which a real stick mirror would not do.
Their actual purpose is unknown.

**Nintendo Switch mode is equally inert.** iControl exposes a PC/Nintendo Switch mode toggle
The toggle sequence:

```
02 36                       (toggle - flips whichever mode is currently active, no
                              mode parameter; sending this alone does not switch anything -
                              LED color and gyro-mouse behavior both stay on the previous mode)
02 11 <01 if entering NS, 00 if entering PC> 14   (status query, mode-flag byte in position 2
                              alternates with direction)
02 15                        (second query - completes the switch)
```

All three are required together, confirmed via LED color and gyro-mouse behavior.
`tools/HidCapture` replicates this via its `toggle` console command, tracking direction so the
`02 11` parameter alternates correctly. In NS mode, rotating the controller vigorously in all
directions for ~70 seconds (including with the physical gyro switch also on) produces zero byte
changes on the `0x25` report - as inert as PC mode. iControl's own 4-part `02 12 00 05 ...`
profile/config dump that follows a real mode switch on the wire is deliberately not replicated
here - it looks like UI resync, not something the firmware requires.

**The gyro toggle switch only gates mouse output on endpoint 0x83; it has no effect on endpoint
0x84.**

### Confirmed via iControl's own hidden factory-test screen

iControl ships a manufacturing/QA-only screen with a live gyroscope/accelerometer readout
(`ac_x`/`ac_y`/`ac_z`, `gy_x`/`gy_y`/`gy_z`), never exposed in the normal consumer UI. Reaching
it and testing it directly gives an independent, stronger confirmation of the conclusion above.

**How to reach it** (tested against the installed iControl build under
`D:\Utilities\Beitong` in this investigation - paths/behavior may differ across versions):

1. iControl's `resources/app/00000000.asar` is an Electron ASAR archive containing the app's
   unminified-enough JS; `betoplib.GetData("config", key)` calls throughout it read directly
   from `config.ini`'s `[config]` section (confirmed: `Lang=en` in that file matches
   `GetData("config","Lang")` reads verbatim).
2. The app's root Vue component has a computed property `inside` that returns
   `betoplib.GetData("config", "APP1")` and switches the *entire app* (main window and tray
   menu both) into one of several manufacturing-only modes when set: `inside === "Factory"`
   (with a device-generation flag also true) renders a `newFactoryTest` component instead of
   the normal controller UI; other observed values include `"Factory2"` (`newSemiFinished`) and
   several sibling top-level components (`Production`, `Modularization`, `secondGeneration`)
   gated the same way. This is read once via a native call, not reactive to a live config
   edit - a full app restart is required after changing it.
3. Adding `APP1=Factory` under `config.ini`'s `[config]` section and restarting iControl
   (fully closed first, including any lingering `betopgame.exe`/`aipan70.exe` processes) opens
   the factory-test screen directly in place of the normal app.
4. This screen's button/D-pad/joystick/trigger panels all responded live and correctly during
   testing. The gyroscope/accelerometer panel stayed at a flat `ac_x/y/z: 0, gy_x/y/z: 0`
   regardless of physically rotating/tilting the controller, pressing the screen's own
   "Retest" button, or toggling the controller's physical gyro switch off and on. A real,
   powered accelerometer at rest should never read exactly zero on every axis simultaneously
   (gravity alone guarantees a nonzero baseline on at least one axis) - this is a manufacturer's
   own internal QA tool built specifically to validate this sensor, showing no response under
   every condition tested.
5. Revert by removing the `APP1=Factory` line from `config.ini` and restarting iControl again.

**Correction (later investigation):** the conclusion originally drawn here - that the IMU
might be unpopulated or unwired - was wrong, and was already contradicted by the device's own
working gyro-as-mouse output (the cursor demonstrably moves when tilting with the gyro toggle
on). The factory screen's flat zeros have a mundane explanation found in iControl's own
source: its test-mode init is explicitly skipped on wireless-dongle connections
(`test_mode_usedongle` flag), and the underlying `enterTestMode` (`02 17 01`) /
`getTestData` (`02 35`) commands get no reply at all over the dongle when sent directly. The
gyro/accel readout is a dead diagnostic path over this connection type - the sensor itself
works. See `docs/config-protocol.md` for the follow-up investigation that confirmed live gyro
data can be obtained on Interface 4 via the on-controller gyro-to-stick mapping
(`feeling_map_type`), including what does and does not work for raw gyro output.

---

## Gyro Output Mode (Mouse vs. Button)

A separate setting under Gyro Settings in iControl (distinct from PC/Nintendo Switch mode
above) selects whether the processed gyro output drives mouse movement (endpoint 0x83, see
above) or a mapped virtual button press ("Button" mode). The toggle command is `02 13 00
00...` - same no-parameter-toggle shape as `02 36`. Unlike `02 36`, the device just acknowledges
it with a plain `02 13` echo and normal traffic resumes immediately, with no observed required
follow-up handshake.

**Sending `02 13` from PanguBridge's own code has never worked**, despite confirming
byte-for-byte parity with what iControl sends and confirming the device acknowledges the
command the same way it acknowledges iControl's. The mode itself never changes. Likely cause:
some longer-lived session precondition from iControl's full startup handshake that has not been
identified. Not pursued further - a user can switch to Button mode once via iControl (confirmed
to persist without iControl running, below), then never need iControl again.

### Button mode mechanism

Button mode maps gestures (shake, tilt in various directions) to virtual button presses. The
mechanism is a live OR-bitmask of "which button slot is currently active," split across two
bytes using the device's own existing bit conventions, independent of real physical button
presses (which continue through their normal channels, `[8]`/`[9]`/`[31]`, unaffected):

- Byte `[18]` uses the D-pad's own convention (`0x01/02/04/08` = Up/Down/Left/Right) for
  D-pad-mapped gestures.
- Byte `[19]` uses the standard button byte `[9]`'s own convention (`0x01/02/10/20/40/80` =
  LB/RB/A/B/X/Y) for gestures mapped to those slots.
- Combo values OR together correctly when multiple gestures are mapped simultaneously (e.g.
  `0xA2` = `0x80|0x20|0x02` = Y+B+RB).

A real physical button press fires all three of byte `[9]` (standard button byte), byte `[31]`
(the A/B/X/Y mirror documented in `docs/hid-report-format.md`, using its own `0x01/02/04/08`
encoding), and the corresponding bit in `[18]`/`[19]` together. A gesture-mapped press only
lights up `[18]`/`[19]` - `[9]` and `[31]` stay at zero. This makes byte `[18]`/`[19]` a
synthesized "effective button" output (real presses plus whatever gesture is mapped to that
slot), not a gesture-agnostic motion-detection channel; the two cases remain distinguishable in
software (real press = all three bytes set; gesture = `[18]`/`[19]` alone).

**Persists without iControl running.** With the device already in Button mode and gesture
mappings configured via iControl, closing iControl entirely and testing with `tools/HidCapture`
alone shows the device stays in Button mode and continues responding to gestures - the mode and
mappings live on the device itself.

---

## Endpoint 0x81 (Interface 0 - XInput)

Standard 20-byte XInput report. Idle:
```
00 14 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

FN button appears here as Guide (byte 3 bit 0x04):
```
00 14 00 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

Standard XInput layout:
- Byte 0: Report ID (0x00)
- Byte 1: Packet size (0x14 = 20)
- Bytes 2-3: wButtons bitmask
- Byte 4: Left trigger (0-255)
- Byte 5: Right trigger (0-255)
- Bytes 6-7: Left stick X (int16)
- Bytes 8-9: Left stick Y (int16)
- Bytes 10-11: Right stick X (int16)
- Bytes 12-13: Right stick Y (int16)
- Bytes 14-19: Reserved

---

## Wireshark Capture Notes

- USBPcap3 was the correct interface for this machine.
- Device address varies between sessions - always enumerate by VID/PID, not a fixed address.
- Useful display filter for Interface 4 input reports:
  `usb.dst == host && usb.device_address == 5 && usb.endpoint_address == 0x84`
- Filter for descriptor enumeration:
  `usb.bDescriptorType == 0x04`
- Capture with no endpoint/snap-length restriction - a narrow filter or small snap length can
  capture zero payload bytes.
