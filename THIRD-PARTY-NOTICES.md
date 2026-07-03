# Third-Party Notices

PanguBridge bundles the following third-party components. This notice satisfies each one's
license attribution requirement.

## HIDMaestro

`PanguBridge/lib/HIDMaestro.Core.dll` is redistributed unmodified from
[hifihedgehog/HIDMaestro](https://github.com/hifihedgehog/HIDMaestro), licensed under the MIT
License:

```
Copyright (c) 2026 HIDMaestro Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

See also [PadForge](https://github.com/hifihedgehog/PadForge), an open-source HIDMaestro-based
controller remapper whose source was referenced (not copied) when building PanguBridge's
DualSense Edge output - see `docs/architecture.md`. PadForge is licensed under Creative Commons
Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0), a non-commercial
content license, not a typical software license. No PadForge code is included in PanguBridge in
any form - it was read only to understand how HIDMaestro's DualSense Edge profile is driven, and
every line of PanguBridge's own implementation was written independently. Ideas and architecture
aren't subject to copyright the way literal code is, so this doesn't trigger any obligation
under PadForge's license, but it's called out explicitly here given how restrictive that license
is.

## HidSharp

`HidSharp` (NuGet package, used for reading the Pangu's HID interface) is licensed under the
Apache License, Version 2.0. Copyright 2010-2025 James F. Bellinger. Full license text:
<https://www.apache.org/licenses/LICENSE-2.0>. No NOTICE file is bundled with the package, so
none is reproduced here per the license's own terms.

## Hardcodet.NotifyIcon.Wpf

`Hardcodet.NotifyIcon.Wpf` (NuGet package, used for the system tray icon) is licensed under the
Code Project Open License (CPOL) 1.02. Copyright (c) 2009-2021 Philipp Sumi (authors: Philipp
Sumi, Robin Krom, Jan Karger). Full license text:
<https://www.codeproject.com/info/cpol10.aspx>. Unmodified; original copyright/attribution
notices are preserved per the license's terms.

## NAudio

`NAudio` and its sub-packages (NAudio.Core, NAudio.Wasapi, used for Audio Auto Haptics' loopback
capture) are licensed under the MIT License. Copyright 2020 Mark Heath:

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

This is a separate notice from the DS5Dongle entry below - DS5Dongle is credited for the
audio-haptics *algorithm* PanguBridge's own code implements; NAudio is credited here for the
actual library binaries PanguBridge redistributes to run that code against Windows' audio APIs.

## DS5Dongle

`PanguBridge/Controllers/AudioAutoHapticsCapture.cs`'s DSP chain (low-pass filter, envelope
follower, soft clip) is ported from the "Audio Auto Haptics" feature of
[loteran/DS5Dongle](https://github.com/loteran/DS5Dongle) (a fork of
[awalol/DS5Dongle](https://github.com/awalol/DS5Dongle)), licensed under the MIT License:

```
MIT License

Copyright (c) awalol
Copyright (c) 2026 loteran - auto-haptics audio engine, battery color LED,
    runtime wake toggle, PyQt6 config app, PipeWire loopback integration,
    and all additions since the fork

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

PanguBridge's implementation is an independent port of the algorithm's math (frequency-domain
coefficients, attack/release envelope, soft-clip shape) into C#, targeting NAudio's WASAPI
loopback capture instead of DS5Dongle's Raspberry Pi Pico firmware and Bluetooth link - it does
not reuse any of DS5Dongle's source code directly.
