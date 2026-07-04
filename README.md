# PanguBridge

A Windows app that reads input from a **Beitong Pangu** wireless controller and re-emits it as a
virtual **PlayStation DualSense Edge** controller - buttons, sticks, triggers, D-pad, the 9 extra buttons (AI/FN/Profile/M1-M4/LM/RM),
and rumble. The Dualsense Edge was chosen since it is recognized natively by Steam Input and shouldn't need manual configuration.

Beitong's app is not needed for this to function, though running their app doesn't harm anything either.

In the spirit of transparency, note that this application was written with the help of an LLM. I'd rather be forward
about that rather than try to hide it.

---

## Table of Contents

- [Features](#features)
- [What It Does](#what-it-does)
- [Requirements](#requirements)
- [Installation](#installation)
  - [HIDMaestro](#additional-info-hidmaestro)
  - [vJoy (Legacy)](#additional-info-vjoy-legacy)
- [Steam Input: What to Expect](#steam-input-what-to-expect)
- [Limitations](#limitations)
- [Building From Source](#building-from-source)
- [Technical Notes: USB Protocol Reverse-Engineering](#technical-notes-usb-protocol-reverse-engineering)
- [Acknowledgments](#acknowledgments)

---

## Features

1. Steam Input support - all buttons can be mapped independenly, including special buttons.
2. Light/LED control is passed along to Steam Input.
3. Many more rumble options than the stock app.
4. Simulated Adaptive Trigger option, using the motors in the triggers (impulse triggers).
5. Audio Auto Haptics option, which uses audio to drive haptic rumble (ported from DS5Dongle). 

---

## What It Does

The Pangu controller ships with a Windows driver that exposes it as a generic XInput device -
functional, but it strips out the 9 extra buttons (AI/FN/Profile/M1-M4/LM/RM), and Steam Input
only ever sees "Xbox controller," with no way to remap or even see those extra inputs at all.

PanguBridge reads the controller's **vendor-defined HID interface** directly - the same one
Beitong's own iControl app uses - decodes the physical controls, and re-emits the whole thing as a virtual
DualSense Edge controller via [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro).
Steam Input recognizes a DualSense Edge natively: every button gets a real name and icon,
rumble works out of the box, and there's no mapping file to fiddle with.

A legacy vJoy-based output mode also exists for setups that can't or don't want to use
HIDMaestro, but that requires manual Steam configuration and will not have rumble - see
[vJoy (Legacy)](#vjoy-legacy) below.

---

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- A Beitong Pangu (BTP-PG01A) and its wireless USB dongle

No other drivers are required for the recommended (HIDMaestro) path - it's bundled with the
app. The legacy vJoy path needs a separate driver install; see below.

---

## Installation

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) if
   you don't already have it.
2. Download the latest Pangu Bridge release and extract it anywhere.
3. Close Beitong's iControl app if it's running (including closing it from the taskbar).
4. Run `PanguBridge.exe`. It requests administrator privileges on launch - HIDMaestro needs
   this to create the virtual controller.
5. On the Status page, press the Install Driver button for HIDMaestro (unless you wish to use vJoy).
6. Disable the Pangu's default "Xbox 360 Controller for Windows" in device manager.

### Additional Info: Disabling the Pangu's built-in Xbox 360 device

The Pangu exposes a standard Xbox 360-compatible XInput device alongside its vendor HID
interface. With Pangu Bridge's virtual DualSense Edge also present, Steam (and some games) will
see **two** controllers for the same physical device. Inputs will come through on both, which can
cause games to see double inputs, steam to see double home buttons, and generally just cause issues.

**Recommended:** open **Device Manager** → find the Pangu's **Xbox 360 Controller for Windows** entry
(usually under "Sound, video and game controllers" or "Human Interface Devices") → right-click → **Disable device**.
Let Windows restart when asked. This only affects the redundant XInput node, as PanguBridge reads the controller
through a completely separate USB interface and is unaffected.

### Additional Info: HIDMaestro

This is the default backend and needs no separate driver download - HIDMaestro's runtime is
bundled with PanguBridge itself.

1. On first launch, click **Install Driver** under the HIDMaestro header in Status. This is a
   one-time step (a UMDF2 driver gets registered in Windows' driver store) and persists across reboots.
2. Go to the **Status** tab and press **Start**.
3. Open Steam - it will detect a **DualSense Edge Wireless Controller** automatically, with
   full button naming, rumble, and no configuration needed.

That's it. Everything else (button mapping, Steam Input setup) is handled automatically
because the virtual controller presents itself as a DualSense Edge, and Steam can't tell the difference.

### Additional Info: vJoy (Legacy)

A fallback output mode for setups that can't use HIDMaestro. **Not recommended** for regular
use - it has no rumble support (see [Technical Notes](#technical-notes-usb-protocol-reverse-engineering)
for why grip and trigger rumble can't cleanly coexist on a generic DirectInput device the way
they can on a real DualSense Edge identity), Steam Input requires manual setup instead of recognizing the
controller automatically, and I could not reliably disconnect a vJoy device when not in use (the vJoyConf enable/disable
option just didn't work for me consistently).

In broad strokes:

1. Install [vJoy](https://github.com/jshafer817/vJoy) (or the actively-maintained
   [BrunnerInnovation fork](https://github.com/BrunnerInnovation/vJoy)).
2. In PanguBridge's **Options** tab, switch **Virtual Controller Backend** to **vJoy**.
3. Open the **vJoy (Legacy)** tab and click **Automatically Configure vJoy Device** (or
   configure it manually with vJoyConf: 19 buttons, the X/Y/Z/Rx/Ry/Rz axes, and 1 continuous
   POV hat).
4. Customize the button-to-vJoy-slot mapping if you want, and Save.
5. In Steam, set the device up as a generic controller, then use the **Steam Input** tab's
   "Copy to Clipboard" button and Steam's own "Paste from Clipboard" option during controller
   setup - see [Steam Input: What to Expect](#steam-input-what-to-expect) below.

---

## Steam Input: What to Expect

**On HIDMaestro (recommended):** nothing to do. Steam Input recognizes a real DualSense Edge
Wireless Controller identity automatically - correct button names/icons, working rumble, no
mapping file needed. Some extra buttons will need to be mapped to touchpad buttons, which isn't intuitive
but at least it works!

**On vJoy (legacy):** Steam Input treats the controller as a generic DirectInput device. To get
special keys working, do the following:

1. In Steam, go to **Settings → Controller → General Controller Settings** and let it detect
   PanguBridge's vJoy device as a generic gamepad.
2. In PanguBridge's **Steam Input** tab (under vJoy (Legacy)), click **Copy to Clipboard**.
3. During Steam's own controller configuration flow, click its **Paste from Clipboard**
   button when prompted.

There were too many limitations for rumble to work well going down this avenue, so I dropped it.

---

## Limitations

1. Gyro. Still investigating options. Potentially NS mode over bluetooth, but that has a bunch of negatives. PC NS
mode over the dongle appears to be a dead end.
2. Resolution and Polling Rate. The raw report used has a 250hz polling rate. Analog inputs are 8bit resolution (0-255).
3. Real Adaptive Triggers. This controller doesn't have them. The simulation setting is neat though, and I use it myself for games
that pass Adaptive Trigger output through Steam Input properly. Which isn't a lot, but some do!
4. Module Layouts. Any combination of 2 analog sticks, 1 dpad, and 1 ABXY works, you can freely move the modules anywhere.
Sadly however the raw report this relies on currently duplicates 'like' modules. So having two ABXY for example just
puts out the same bits regardless of which A button you press.
5. You can't combine mutliple touchpad touch inputs for fancy Steam Input shenanigans, they just morph into one button. So
if you want to be able to hold LM and RM together for a special layout, you will need to change the Dualsense mapping (in the
HIDMaestro section).
6. Beitong could change the raw report at anytime with an update, and this could stop working.
7. Only one controller is converted for the time being, I don't have two of these controller to test multiple.
8. Haptic Rumble is lost. The DualSense can read three rumble types: Normal, Haptic, and Adaptive Triggers (rumble-ish). Haptic 
is not currently converted to Normal, though I am still investigating this.

---

## Building From Source

Requires the .NET 10 SDK and Windows (the project targets `net10.0-windows` and uses WPF).

```bash
git clone <this repository>
cd PanguBridge
dotnet build PanguBridge.sln -c Release
```

The build output lands in `PanguBridge/bin/x64/Release/net10.0-windows/`. Ship it with
`portable.txt` present (already copied into the output directory by the build) to keep all
config data (`settings.json`) next to the executable instead of `%APPDATA%`.

Project layout:

```text
PanguBridge/
├── Controllers/       HID read/write, HIDMaestro output, vJoy output
├── Mapping/            vJoy button-mapping model
├── Steam/               SDL/Steam Input mapping-string generation
├── Views/                WPF tab views (Status, Options, Input Test, HIDMaestro, vJoy Legacy)
└── lib/HIDMaestro.Core.dll   Bundled HIDMaestro SDK
```

See `docs/architecture.md` for the full set of design decisions and their rationale.

---

## Technical Notes: USB Protocol Reverse-Engineering

All of this was reverse-engineered with Wireshark + USBPcap against Beitong's own iControl
app, since the Pangu has no public protocol documentation. Full capture methodology and
byte-level detail live in `docs/` - this is a summary.

### Device

```
VID: 0x20BC   PID: 0x5162   Product string: "BTP-PG01A XINPUT DONGLE"
```

The dongle exposes 5 USB interfaces; PanguBridge only uses **Interface 4** (vendor-defined
HID, usage page `0xFF80`) - the same one iControl uses for everything except gyro-as-mouse
output. Reading only this interface means PanguBridge never touches the OS's XInput driver
stack at all, which is what lets the "disable the Xbox 360 device" step above work cleanly
without breaking input.

```
Endpoint IN:  0x84 (64 bytes, 4ms interval / ~250Hz)
Endpoint OUT: 0x02 (64 bytes, 4ms interval)
```

### Getting the interface to talk

Interface 4 sends nothing until the host opens it and writes a specific enable command - without
this, reads block forever:

```
Enable (send once): 02 37 00 00 ... (64 bytes, rest zero)
Keepalive (every 250ms, alternating): 02 25 00 ... / 02 21 00 ...
```

### Input report (`0x25` reports only - `0x21`/`0x31`/`0x11`/`0x37` are heartbeats/status, ignored)

| Byte | Meaning |
|------|---------|
| `[0]` | Report ID, always `0x02` |
| `[1]` | Report type - `0x25` = normal input (process it), everything else = ignore |
| `[2]` / `[3]` | Left stick X / Y - `0x80` center. **X is inverted** (high=left, low=right) |
| `[4]` / `[5]` | Right stick X / Y - same convention as left |
| `[8]` | D-pad + Start/Back/L3/R3 bitmask |
| `[9]` | LB/RB/Fn/Profile/A/B/X/Y bitmask |
| `[10]` | AI (bits 6+7 both set) / M1-M4 bitmask |
| `[11]` | LT/RT digital + LM/RM bitmask |
| `[16]` / `[17]` | Left / Right trigger analog, `0x00`-`0xFF` |

Full bit-level detail (including the redundant mirror-copy bytes the firmware also sends) is
in `docs/hid-report-format.md` and `docs/button-map.md`.

### Rumble: two independent motor pairs sharing one report type

The Pangu has **four** physical rumble motors - two grip, two trigger - driven by
different commands, both sent on the same OUT endpoint as everything else:

```
Grip (continuous 0-255 magnitude):
  02 16 <Lmag> <Rmag> <Lmag> <Rmag> 00 ... 00

Trigger (5 discrete levels, 0-4):
  Configure (sent once per level change):
    02 62 00 0C 3C 96 A5 C3 01 FF 00 9A 00 <Llevel×0x10> <Rlevel×0x10+0x04> 04 00 FF FF 00 ...
  Fire/stop (separate use of the SAME report type grip uses):
    02 16 00 00 <Lfire=0xFF/0x00> <Rfire=0xFF/0x00> 00 ... 00
```

The important catch: grip's report and the trigger fire report are **the same report type**
(`0x16`) and overlap the same byte range - grip's real value lives in bytes `[2]`/`[3]`
(mirrored into `[4]`/`[5]`), while the trigger fire flags live in `[4]`/`[5]`. The device has
no concept of a partial update - every `0x16` write is the complete truth for all 4 bytes at
once. PanguBridge sends grip and trigger as **one combined report** (grip's real magnitude in
`[2]`/`[3]`, trigger's fire flags in `[4]`/`[5]`) specifically to avoid one silently
overwriting the other when both need to be active simultaneously - see `docs/rumble.md` for
the full investigation and `HidReader.TrySendMotors`.

---

## Acknowledgments

- [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) - the virtual-controller SDK this
  project's DualSense Edge output is built on. `PanguBridge/lib/HIDMaestro.Core.dll` is
  redistributed unmodified under its MIT License - see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- [PadForge](https://github.com/hifihedgehog/PadForge) - an open-source HIDMaestro-based
  controller remapper whose source was used as reference for driving the DualSense Edge profile.
- [DS5Dongle](https://github.com/loteran/DS5Dongle) - its MIT-licensed "Audio Auto Haptics"
  algorithm (bass + transient detection driving rumble from system audio) was ported into
  Audio Auto Haptics - see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- [HidSharp](https://www.zer7.com/software/hidsharp) - reads the Pangu's HID interface.
- [NAudio](https://github.com/naudio/NAudio) - WASAPI loopback audio capture for Audio Auto
  Haptics.
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) - the system tray icon.
