# Steam / SDL Integration

How PanguBridge registers itself with Steam Input and SDL so all buttons are named.

This only applies to the **vJoy (Legacy) backend**. The HIDMaestro backend presents as a real
DualSense Edge identity, which Steam Input recognizes natively with no manual mapping step.

---

## The problem

vJoy creates a generic unnamed DirectInput joystick. Without a mapping, Steam Input shows it as
"Generic Gamepad" with buttons labeled 1, 2, 3..., and SDL games have no named bindings.

## Mechanism: clipboard paste into Steam's own controller setup

Steam Input does not read `gamecontrollerdb.txt` from any location, including
`controller_base` (which contains `.vdf` template files Valve controls, not the SDL text
format) or a per-user `userdata` copy. That text-file convention is read by individual
SDL2/SDL3-linked games directly, not by Steam Input's own controller-configuration screen.

Steam Input's manual controller setup has a built-in "Paste from Clipboard" / "Copy to
Clipboard" feature for SDL mapping strings. Starting a manual controller configuration in
Steam and pasting `SteamMappingWriter.BuildMappingString(...)`'s output applies it directly -
no file write, no elevation, no Steam install path or user ID detection needed. This is the
only mechanism PanguBridge uses.

The `SDL_GAMECONTROLLERCONFIG` environment variable remains a separate option for non-Steam SDL
games (SDL reads it at startup, as a Windows system environment variable requiring elevation) -
not currently exposed in the app, since it's unrelated to Steam Input's own
controller-configuration screen and only matters for SDL games launched outside Steam.

Long-term option: submit a PR to https://github.com/gabomdq/SDL_GameControllerDB. Once merged,
it ships with every future SDL/Steam update with no per-user setup step.

---

## Mapping string format

```
[GUID],[Name],platform:Windows,[button mappings...]
```

Example:
```
0300000034120000adbe000000000000,Pangu Bridge Virtual Controller,platform:Windows,a:b0,b:b1,x:b2,y:b3,leftshoulder:b4,rightshoulder:b5,start:b8,leftstick:b6,back:b9,rightstick:b7,guide:b10,misc1:b11,misc2:b17,misc3:b18,misc4:b12,paddle1:b13,paddle2:b15,paddle3:b14,paddle4:b16,leftx:a0,lefty:a1,lefttrigger:a2,rightx:a3,righty:a4,righttrigger:a5,dpup:h0.1,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,
```

### Button slot cap: misc1-misc6 only

SDL's controller-mapping schema only defines `misc1`-`misc6` - there is no `misc7`/`8`/`9`.
Steam's clipboard round-trip (paste a mapping string, complete setup, then read it back via
Steam's own "Copy to Clipboard") keeps `misc1`-`misc6` intact but silently drops any
`misc7`-`misc9` entirely. The 9 extra Pangu buttons (AI, FN, Profile, M1-M4, LM, RM) are spread
across `guide`, `misc1`-`misc4`, and `paddle1`-`paddle4` instead - see
`SteamMappingWriter.ButtonBindings` for the exact assignment.

Steam displays `misc1`-`misc6` and `paddle1`-`paddle4` with its own fixed, uneditable names
regardless of what is sent - the mapping format carries no custom display text, only position.
AI uses `guide` (Steam's own Steam-button icon; there is no separate Guide button on this
hardware) and FN uses `misc1` (Steam's fixed "Screenshot" label/icon fits its quick-capture
role well enough). The remaining buttons (Profile, M1-M4, LM, RM) are split across
`misc2`-`misc4` and `paddle1`-`paddle4`. Steam's displayed names for `paddle1`-`4` do not
follow a simple, predictable pattern - the assignment in `docs/button-map.md` was verified
directly against Steam's own displayed labels (paste mapping string, complete setup, copy the
mapping string back, confirm it matches intent) rather than computed from a rule. If the
hardware's vJoy slot numbers ever change, re-verify the same way.

### GUID derivation

The GUID must match what Steam/SDL compute for the **vJoy virtual joystick**, not the physical
Beitong dongle - PanguBridge does not expose the physical device to Steam at all, only the vJoy
device, so the GUID has to identify that device or Steam silently never applies the mapping.

vJoy's own HID device reports VID `0x1234`/PID `0xBEAD` by default (identical across the
jshafer817 and BrunnerInnovation forks, per `DEVPKEY_Device_HardwareIds`).
`SteamMappingWriter.DetectVJoyVidPid()` detects this dynamically by enumerating HID devices for
one whose product name contains "vJoy", falling back to the hardcoded default if not found.

`SDL_JoystickGUID` is a 16-byte, all-little-endian layout: `03000000` (bus type=USB + 2 reserved
bytes) + VID bytes (reversed) + `0000` (reserved) + PID bytes (reversed) + `0000` (reserved) +
`0000` (version, unused here) + `0000` (reserved).

---

## GUI - Views/SteamIntegrationView.xaml ("Steam Input" tab)

- Short numbered instructions: set up the controller as a generic gamepad in Steam's Controller
  Settings, click "Copy to Clipboard" here, then click Steam's own "Paste from Clipboard"
  button when prompted during that setup.
- A read-only mapping-string `TextBox` (`SteamMappingWriter.BuildMappingString(...)`), built
  fresh from the current profile each time the tab is constructed.
- A single "Copy to Clipboard" button (`Clipboard.SetText(...)`).
- A small status line confirming the copy happened.
