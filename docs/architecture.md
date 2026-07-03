# Architecture

## Overview

PanguBridge is a single-process Windows WPF tray app. It reads every control from the Pangu's
HID Interface 4 (see `docs/hid-report-format.md`) and drives one of two virtual controller
backends:

- **HIDMaestro (default)** - presents as a real DualSense Edge identity, recognized natively by
  Steam Input with full button/axis labeling, real proportional rumble, Adaptive Trigger
  Simulation, and LED color forwarding. Requires the bundled HIDMaestro driver, installed via
  the app's own Install Driver button.
- **vJoy (Legacy)** - a generic DirectInput joystick, requiring a separate vJoy driver install
  and a manually-pasted SDL mapping string in Steam Input (see `docs/steam-integration.md`) for
  named buttons. No rumble support.

Only one backend is ever active at a time, selected in Options.

---

## Virtual Output: HIDMaestro vs. vJoy

**HIDMaestro** is preferred because it presents as a real DualSense Edge to Steam Input, giving
native button/axis labeling, artwork, and real proportional rumble with no manual setup.
`HidMaestroOutput` drives it via the `dualsense-edge` HIDMaestro profile - see
`docs/rumble.md` and `docs/led.md` for the rumble and LED wire protocols this profile exposes.

**vJoy** exists as a fallback for users who don't want to install the HIDMaestro driver, or
where it's unavailable. It was chosen over ViGEm DS4 impersonation because the DualShock 4 has
only 2 buttons beyond the standard Xbox layout (PS button, Touchpad Click) - insufficient for
the Pangu's 9 extra buttons (AI, FN, Profile, M1-M4, LM, RM). Impersonating a controller Steam
recognizes with a matching extra-button count (e.g. Flydigi's Vader series) was considered and
rejected: SDL identifies Flydigi controllers via a bidirectional get-info handshake (`0xEC`),
which would require implementing the full Flydigi protocol plus a custom virtual HID driver
(ViGEm only handles output) - too complex for the payoff. vJoy gives unlimited buttons/axes,
DirectInput support SDL and Steam Input can read, and a custom SDL mapping string gives full
named button support in Steam Input, at the cost of requiring a separate driver install, manual
pre-configuration (button/axis count), no native Steam Input artwork, and no rumble support.

---

## Rumble

Only the HIDMaestro backend supports rumble. `HidMaestroOutput` receives real proportional
rumble motor magnitudes from Steam/games via HIDMaestro's `OutputDecoded` event, and
`PanguEngine` routes them (per the configurable `RumbleMode`) to `HidReader.TrySendMotors`,
which writes the Pangu's native rumble wire protocol directly over Interface 4's existing OUT
endpoint (`0x02`) - the same stream already used for the enable command and keepalive. See
`docs/rumble.md` for the full wire protocol, Adaptive Trigger Simulation, and known device
limitations.

The vJoy backend has no rumble support - Steam Input can only send an on/off signal to generic
DirectInput devices, and no implementation is built for it.

---

## Process Architecture: Single WPF Tray App

Single WPF process - no Windows Service, no IPC. A background service would only be needed if
the controller had to work before user login or survive GUI crashes independent of the input
pipeline, which doesn't matter for a gaming peripheral (DS4Windows, reWASD, and Steam Input
itself all ship as single user-session apps for the same reason).

**Why WPF:**
- Native Windows look and feel, with system tray support via `Hardcodet.NotifyIcon.Wpf`.
- All hardware interop (HidSharp, vJoy SDK, HIDMaestro SDK) is .NET-native. A web-based UI
  (Electron/WebView2) would either require native Node addons for vJoy/HIDMaestro or reintroduce
  a backend/IPC split, for UI screens (status view, mapping editor, dropdowns, forms) that are
  WPF's exact sweet spot.

**Autostart:** an elevated Task Scheduler task (`Autostart.cs`, `/RL HIGHEST /SC ONLOGON`), not
the HKCU Run key. `app.manifest` requests `requireAdministrator` (HIDMaestro needs it for both
`InstallDriver()` and `CreateController()`), and a plain Run-key entry does not auto-elevate -
Windows would either show a UAC prompt at every login or the launch would simply fail. A Task
Scheduler task with `/RL HIGHEST` launches already elevated with no interactive prompt.

---

## Config: Portable Mode Default

`portable.txt` ships next to the binaries by default. Data goes to `<exedir>\data\`. Deleting
`portable.txt` switches to `%APPDATA%\PanguBridge\` instead - see `ConfigPaths.cs`.

---

## HidHide (deferred)

Not yet implemented. Hiding an XInput device inside a composite device caused issues. Without HidHide, 
games can see both the real Pangu XInput device and the active virtual controller, causing double input. 
Planned behavior: call `HidHideCLI.exe` on startup to hide the real device and whitelist PanguBridge itself, 
with a GUI toggle to disable and a warning if HidHide isn't installed. Until built, the workaround is
manually disabling the stock XInput device from the controller.

---

## Thread Model

```
Main thread:  WPF UI thread (tray icon, windows)
HidReader:    dedicated read loop (blocking read on Interface 4's IN endpoint, natural ~4ms
              rate) plus a separate keepalive writer thread (250ms interval)
HIDMaestro:   dedicated real-time-priority submit thread, rate configurable in Options (one of
              250/500/750/1000 Hz, default 1000 - HidMaestroOutput.SubmitThreadProc), only
              running while the HIDMaestro backend is active
PanguEngine:  a ~60 Hz rumble gate loop (RumbleGateLoop), only running while a gated
              RumbleMode, Adaptive Trigger Simulation, or Audio Auto Haptics is active - see
              docs/rumble.md
vJoy:         output updates happen synchronously on HidReader's read-loop thread via
              PanguEngine.OnHidStateChanged, only while the vJoy backend is active
```

A `CancellationToken` flows through the read loop, keepalive loop, and rumble gate loop,
cancelled on `PanguEngine.Stop()`.

---

## Error Handling Philosophy

- Controller not found → wait and retry every 2 seconds.
- Controller disconnects → tear down readers, wait, reconnect.
- vJoy not installed (vJoy backend selected) → show a clear message in the GUI; the app cannot
  run its core function on that backend without it.
- HIDMaestro driver not installed (HIDMaestro backend selected) → show a clear message with an
  Install Driver button; the app cannot run its core function on that backend without it.
- HidHide not installed → log a warning, continue without device hiding (deferred phase).

---

## Steam/SDL Integration

Only relevant for the vJoy (Legacy) backend - the HIDMaestro backend's DualSense Edge identity
is recognized by Steam Input natively, with no manual mapping step.

The working mechanism is Steam Input's own "Paste from Clipboard" feature for SDL mapping strings: 
the app builds a mapping string from the current profile and shows a "Copy to Clipboard" button, and 
the user pastes it into Steam's own manual controller setup. No file write, no elevation, no Steam
install path or user ID detection. See `docs/steam-integration.md` for the full mapping string
format and `docs/button-map.md` for the button assignment table.
