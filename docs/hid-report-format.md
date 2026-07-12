# HID Report Format - Interface 4 (0xFF80)

Implementation reference for `HidReader.cs` / `GamepadState.cs`.

**This interface carries every input the controller has** - both sticks, both triggers, the
D-pad, all standard buttons, and the 9 vendor-defined extra buttons (AI/FN/Profile/M1-M4/LM/RM),
all in the same 0x25 report. PanguBridge no longer reads XInput at all; see "Full Report
Decode" below for how this was reverse-engineered and `docs/button-map.md` for the
consolidated byte/mask/vJoy-button table.

---

## Device

- VID: `0x20BC`, PID: `0x5162`
- Interface: 4
- Usage Page: `0xFF80`
- Endpoint IN: `0x84`
- Endpoint OUT: `0x02`
- Report size: 64 bytes

---

## Finding the Device in HidSharp

```csharp
using HidSharp;

var device = DeviceList.Local
    .GetHidDevices(vendorID: 0x20BC, productID: 0x5162)
    .FirstOrDefault(d =>
        d.DevicePath.Contains("MI_04", StringComparison.OrdinalIgnoreCase));

// Fallback: match by usage page if path matching fails
// d.TryGetUsagePage(out int usagePage) && usagePage == 0xFF80
```

---

## Startup Sequence

The write buffer is 64 bytes, not 65 - size it from `device.GetMaxOutputReportLength()` rather
than hardcoding it (see "HidSharp Notes" below); a mismatched buffer size causes every write to
fail with `ERROR_INVALID_PARAMETER`.

```csharp
var stream = device.Open();
stream.ReadTimeout = 1000; // allow clean shutdown

int outputLength = device.GetMaxOutputReportLength(); // 64 on real hardware, not 65

// 1. Send enable command (required - without this, Read() blocks forever)
var enable = new byte[outputLength];
enable[0] = 0x02; // report ID - same byte[0] convention as reads, see below
enable[1] = 0x37; // enable command
stream.Write(enable);

// 2. Start keepalive thread (250ms interval, alternating 0x25/0x21)
```

## Keepalive Thread

```csharp
bool toggle = true;
while (!cancellationToken.IsCancellationRequested)
{
    var report = new byte[outputLength];
    report[0] = 0x02;
    report[1] = toggle ? 0x25 : 0x21;
    stream.Write(report);
    toggle = !toggle;
    await Task.Delay(250, cancellationToken);
}
```

---

## Report Structure (64 bytes received)

```
[0]  = 0x02          Report ID - always
[1]  = Report type:
         0x25 = normal input  ← ONLY process these
         0x21 = heartbeat     ← ignore
         0x31 = heartbeat     ← ignore
         0x11 = status        ← ignore
         0x37 = enable resp   ← ignore
[2]  = Left stick X  - 0x80 center, INVERTED (high=left, low=right)
[3]  = Left stick Y  - 0x80 center (low=up, high=down)
[4]  = Right stick X - 0x80 center, INVERTED (high=left, low=right)
[5]  = Right stick Y - 0x80 center (low=up, high=down)
[6]  = Left trigger touched flag  - 0x00 released, 0xFF held at all
[7]  = Right trigger touched flag - 0x00 released, 0xFF held at all
[8]  = Button bitmask: 0x01=DPadUp 0x02=DPadDown 0x04=DPadLeft 0x08=DPadRight
                       0x10=Start  0x20=Back     0x40=L3       0x80=R3
[9]  = Button bitmask: 0x01=LB 0x02=RB 0x04=Fn 0x08=Profile
                       0x10=A  0x20=B  0x40=X   0x80=Y
[10] = Button bitmask: 0xC0=Ai (bits 6+7 both set) 0x01=M1 0x02=M2 0x04=M3 0x08=M4
[11] = Button bitmask: 0x01=LT(digital) 0x02=RT(digital) 0x04=Lm 0x08=Rm
[12..20]
     = the firmware's PROCESSED input block ("keyevent" in iControl's own source, vs the raw
       "keyevent_raw" at [2..11]): [12]=LX' [13]=LY' [14]=RX' [15]=RY' [16]=LT' [17]=RT'
       [18..20]=button bytes, after the controller applies its on-board mappings - stick
       curves/deadzones, button remaps, and motion-sensing (gyro) mixing. Normally tracks the
       raw bytes nearly 1:1 (which is why [16]/[17] were long documented as "the" analog
       triggers), but diverges when an on-board mapping is active: gyro-as-stick mixes gyro
       into [14]/[15] (or [12]/[13]), and gyro Button-mode gestures set bits in [18]/[19]
       without touching raw [9]/[31] (see usb-reverse-engineering.md's Button mode section and
       docs/config-protocol.md's feeling_map_type). Identified from iControl's own parser:
       raw block = report bytes [2..11], processed block = [12..20], confirmed live by writing
       feeling_map_type=1 and watching gyro drive [14]/[15] while [4]/[5] stayed still.
[21..32]
     = per-module data for the 4 swappable modules ("keyevent_ext"): 3 bytes each -
       [21..23]=module0 X,Y,buttons; [24..26]=module1; [27..29]=module2; [30..32]=module3.
       This is why these bytes track the sticks: the stick modules report their own X/Y here.
[31] = (within module3's slot above) also observed repeating A/B/X/Y as 0x01/0x02/0x04/0x08 -
       the face-button module's button byte.
[16] = Left trigger analog (processed)  - 0x00 released, 0xFF full pull
[17] = Right trigger analog (processed) - 0x00 released, 0xFF full pull
[33..34]
     = tracks RX/RY alongside [4..5] and [14..15]; not covered by iControl's parsed structs
       (its keyevent_ext parsing stops at [32]). Exact identity unconfirmed - possibly a
       fifth module slot's X/Y.
[36] = additional mirror byte (button data in an alternate encoding), same family as the
       module bytes above.
[rest] = 0x00/0x80 at rest, unused
```

---

## Button Decoding (0x25 reports only)

```csharp
byte[] report = new byte[64];
int bytesRead = stream.Read(report, 0, 64);

// Filter: only process normal input reports
if (report[0] != 0x02) return;
if (report[1] != 0x25) return;

byte b8  = report[8];
byte b9  = report[9];
byte b10 = report[10];
byte b11 = report[11];

// Standard buttons
bool a            = (b9 & 0x10) != 0;
bool b             = (b9 & 0x20) != 0;
bool x             = (b9 & 0x40) != 0;
bool y             = (b9 & 0x80) != 0;
bool leftShoulder  = (b9 & 0x01) != 0;
bool rightShoulder = (b9 & 0x02) != 0;
bool leftThumb     = (b8 & 0x40) != 0;
bool rightThumb    = (b8 & 0x80) != 0;
bool start         = (b8 & 0x10) != 0;
bool back          = (b8 & 0x20) != 0;
bool dpadUp        = (b8 & 0x01) != 0;
bool dpadDown      = (b8 & 0x02) != 0;
bool dpadLeft      = (b8 & 0x04) != 0;
bool dpadRight     = (b8 & 0x08) != 0;

// Extra buttons
bool ai      = (b10 & 0xC0) == 0xC0; // bits 6+7 both set
bool fn      = (b9  & 0x04) != 0;
bool profile = (b9  & 0x08) != 0;
bool m1      = (b10 & 0x01) != 0;
bool m2      = (b10 & 0x02) != 0;
bool m3      = (b10 & 0x04) != 0;
bool m4      = (b10 & 0x08) != 0;
bool lm      = (b11 & 0x04) != 0;
bool rm      = (b11 & 0x08) != 0;

// Sticks/triggers - see VJoyOutput.FromHidByte / InvertAxis for the vJoy-range conversion
byte leftStickX = report[2], leftStickY = report[3];
byte rightStickX = report[4], rightStickY = report[5];
byte leftTrigger = report[16], rightTrigger = report[17];
```

---

## Verified Raw Captures (for reference)

Extra buttons, from 0x25 reports, showing bytes 9/10/11:

| Button held | b9 | b10 | b11 |
|-------------|-----|------|------|
| Idle | 00 | 00 | 00 |
| AI | 00 | C0 | 00 |
| FN | 04 | 00 | 00 |
| Profile | 08 | 00 | 00 |
| M1 | 00 | 01 | 00 |
| M2 | 00 | 02 | 00 |
| M3 | 00 | 04 | 00 |
| M4 | 00 | 08 | 00 |
| LM | 00 | 00 | 04 |
| RM | 00 | 00 | 08 |

All 9 extra buttons verified with 17-press stress test (zero missed inputs).

Standard controls captured with `tools/HidCapture` (a standalone diagnostic console app -
opens Interface 4 with the same enable/keepalive sequence as `HidReader.cs` and logs only
changed bytes, with user-typed markers to label each test). Every stick direction, both
triggers (digital + full analog sweep), all 4 D-pad directions, all 4 face buttons, both
bumpers, Start/Back, and both stick clicks were exercised individually and cross-checked for
bit collisions against the already-mapped extra-button bytes - none found.

---

## Polling Rate

bInterval = 4ms → ~250Hz natural rate.
`stream.Read()` blocks until data arrives - no sleep needed in the read loop.
Set `stream.ReadTimeout` to allow clean thread shutdown on cancellation.

---

## HidSharp Notes

- Write buffer is **64 bytes on this device** (`device.GetMaxOutputReportLength()`), report ID
  is byte[0] - same convention as reads. Don't hardcode 65; HidSharp does not always prepend an
  extra placeholder ID byte, so always read the actual length from the device.
- Read buffer is **64 bytes** (`device.GetMaxInputReportLength()`), report ID is byte[0]
- Set `ReadTimeout` and `WriteTimeout` on the stream
- Call `stream.Close()` in finally block to release device handle
