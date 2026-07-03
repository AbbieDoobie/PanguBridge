# Button Map - Beitong Pangu BTP-PG01A

Complete mapping of all physical inputs to vJoy output slots. This applies to the **vJoy
(Legacy) backend** - the HIDMaestro backend maps physical inputs to DualSense Edge output
buttons instead, user-editable via the app's HIDMaestro tab (defaults in
`Mapping/HmMapping.cs`).

---

## Physical Button Inventory

### Everything is read from HID Interface 4 (0x25 reports only)

Standard buttons, both sticks, both triggers, the D-pad, and the 9 vendor-defined extra buttons are 
all present in the same Interface 4 report - see `docs/hid-report-format.md` for the full byte map. 
This means PanguBridge does not need the OS's XInput device node, which matters for setups that disable 
that node (e.g. to stop Steam from seeing the controller via XInput).

| Physical | Byte | Mask | vJoy Button |
|----------|------|------|-------------|
| A | 9 | 0x10 | 1 |
| B | 9 | 0x20 | 2 |
| X | 9 | 0x40 | 3 |
| Y | 9 | 0x80 | 4 |
| LB | 9 | 0x01 | 5 |
| RB | 9 | 0x02 | 6 |
| LS click | 8 | 0x40 | 7 |
| RS click | 8 | 0x80 | 8 |
| Start | 8 | 0x10 | 9 |
| Back | 8 | 0x20 | 10 |
| D-pad Up | 8 | 0x01 | POV 0° |
| D-pad Down | 8 | 0x02 | POV 180° |
| D-pad Left | 8 | 0x04 | POV 270° |
| D-pad Right | 8 | 0x08 | POV 90° |
| Left stick X | 2 | 0-255, 0x80 center, **inverted** (high=left, low=right) | Axis X |
| Left stick Y | 3 | 0-255, 0x80 center (low=up, high=down) | Axis Y |
| Left trigger | 16 | 0-255 (0=released, 0xFF=full pull) | Axis Z |
| Right stick X | 4 | 0-255, 0x80 center, **inverted** (high=left, low=right) | Axis Rx |
| Right stick Y | 5 | 0-255, 0x80 center (low=up, high=down) | Axis Ry |
| Right trigger | 17 | 0-255 (0=released, 0xFF=full pull) | Axis Rz |
| AI | 10 | 0xC0 | 11 (None, unmapped) |
| FN | 9 | 0x04 | 12 (None, unmapped) |
| Profile | 9 | 0x08 | 13 (None, unmapped) |
| M1 | 10 | 0x01 | 14 (None, unmapped) |
| M2 | 10 | 0x02 | 15 (None, unmapped) |
| M3 | 10 | 0x04 | 16 (None, unmapped) |
| M4 | 10 | 0x08 | 17 (None, unmapped) |
| LM | 11 | 0x04 | 18 (None, unmapped) |
| RM | 11 | 0x08 | 19 (None, unmapped) |

Bytes 9 and 11 each split cleanly between standard and extra-button bits with zero overlap
(byte 9: 0x01/0x02 standard, 0x04/0x08 extra, 0x10-0x80 standard; byte 11: 0x01/0x02
standard, 0x04/0x08 extra) - the firmware packs both into the same bitmask bytes.

**No separate Guide/Home button exists on this hardware** - see the note on `SourceButton` in
`Mapping/SourceButton.cs`.

### Not Exposed / Out of Scope

| Input | Reason |
|-------|--------|
| Gyro raw data | Inaccessible - firmware processes internally, outputs as mouse on 0x83 |
| Gyro toggle switch | Has no effect on 0x84 reports - only gates mouse on 0x83 |

---

## vJoy Configuration Required

The vJoy virtual device must be configured before the service starts:

| Setting | Value |
|---------|-------|
| Buttons | 19 |
| Axes | X, Y, Z, Rx, Ry, Rz |
| POV Hats | 1 (continuous) |
| POV Hat type | Continuous (not discrete) |

Note: vJoy's `SetContPov`/`SetBtn` indices are 1-based (POV 1, buttons 1-19), matching the
device id itself (1-16).

---

## SDL Button Names for gamecontrollerdb.txt

These are the SDL-recognized named slots used in the mapping string:

| SDL Name | Maps To | vJoy Button |
|----------|---------|-------------|
| `a` | A | b0 |
| `b` | B | b1 |
| `x` | X | b2 |
| `y` | Y | b3 |
| `leftshoulder` | LB | b4 |
| `rightshoulder` | RB | b5 |
| `back` | Back | b8 |
| `start` | Start | b6 |
| `leftstick` | LS click | b7 |
| `rightstick` | RS click | b9 |
| `dpup` | D-pad Up | h0.1 |
| `dpdown` | D-pad Down | h0.4 |
| `dpleft` | D-pad Left | h0.8 |
| `dpright` | D-pad Right | h0.2 |
| `leftx` | Left stick X | a0 |
| `lefty` | Left stick Y | a1 |
| `lefttrigger` | LT | a2 |
| `rightx` | Right stick X | a3 |
| `righty` | Right stick Y | a4 |
| `righttrigger` | RT | a5 |
| `guide` | AI | b10 |
| `misc1` | FN | b11 |
| `misc2` | LM | b17 |
| `misc3` | RM | b18 |
| `misc4` | Profile | b12 |
| `paddle1` | M1 | b13 |
| `paddle2` | M3 | b15 |
| `paddle3` | M2 | b14 |
| `paddle4` | M4 | b16 |

Note: button indices (b0, b1...) are zero-indexed in SDL mapping strings but
vJoy buttons are 1-indexed internally.

**`misc7`/`misc8`/`misc9` are not real SDL fields.** SDL's controller-mapping schema only
defines `misc1`-`misc6`. Steam's own "paste from clipboard" → "copy to clipboard" round-trip
silently drops `misc7`-`misc9` while preserving `misc1`-`misc6` intact. `misc5`/`misc6` go
unused - the assignment above only needs `misc1`-`misc4`, with the rest on `paddle1`-`paddle4`
(back-paddle slots, e.g. Xbox Elite/DualSense Edge controllers), which are real SDL fields.

**The exact slot each button lands on does not follow a predictable rule** (e.g. M1→`paddle1`
rather than `paddle2`, M2→`paddle3` rather than `paddle1`). Steam shows `misc1`-`misc6` and
`paddle1`-`paddle4` with its own fixed, uneditable display names/icons regardless of what SDL
field or physical button is assigned there - the mapping format carries no custom display text,
only position - and those fixed names do not follow a simple "paddle N" pattern. The table
above was verified directly against Steam's own displayed names (paste mapping string,
complete setup, copy the mapping string back, confirm it matches intent). If the hardware's
button layout or vJoy slot numbers ever change, re-verify the same way rather than recomputing
from a formula.

**AI uses `guide` and FN uses `misc1`, both by choice, not because there's a separate
Guide button** (there isn't - see the note on `SourceButton`). Steam renders `guide` with
its own Steam-button-style icon, and FN's "Screenshot" label/icon on `misc1` fits its
quick-capture-ish role well enough.
