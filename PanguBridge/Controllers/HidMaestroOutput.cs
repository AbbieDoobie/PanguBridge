using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using HIDMaestro;
using PanguBridge.Mapping;

namespace PanguBridge.Controllers;

/// <summary>
/// Drives a stock HIDMaestro DualSense Edge virtual controller from the decoded GamepadState
/// and receives real proportional rumble data from Steam/games via OutputReceived.
///
/// Every digital input's DualSense Edge destination is looked up from <see cref="ButtonMap"/>
/// (user-editable via the HIDMaestro tab's mapping table) rather than hardcoded - see
/// ComputeOutputActive. Sticks/triggers are not remappable this way; see AnalogAdjustment for
/// their separate invert/swap handling.
///
/// Rumble: Output Report 0x02 pkt.Data[2]=rightMotor, pkt.Data[3]=leftMotor (0-255 each).
/// Requires HIDMaestro 1.3.17+ and the HIDMaestro driver installed once via the "Install
/// Driver" button on the Status tab (or "Reinstall Drivers" on the HIDMaestro tab). Both
/// InstallDriver() and CreateController() require SeLoadDriverPrivilege (admin) per the SDK's
/// own docs - PanguBridge's own process already runs elevated for its whole lifetime
/// (app.manifest), so neither call needs a separate elevation prompt here.
/// </summary>
public sealed class HidMaestroOutput : IDisposable
{
    private const int TpLeftX   = 480;
    private const int TpRightX  = 1440;
    private const int TpCenterX = 960;
    private const int TpCenterY = 540;

    // The HIDMaestro driver reads the shared-memory input slot at a fixed ~250 Hz regardless
    // of app submission rate. Submitting at 4x that (1000 Hz) guarantees the driver's poll
    // always finds fresh data no matter the phase relationship between our timer and its
    // independent poll cycle - submitting at exactly 250 Hz risks the two drifting in and out
    // of phase, occasionally leaving the driver reading stale data for a full cycle. Matches
    // PadForge's default (github.com/hifihedgehog/PadForge submits a full 63-byte raw report
    // unconditionally every tick at ~1000 Hz). What matters for input latency is the precision
    // of delivery, not just the nominal rate - see SubmitThreadProc, which achieves this via a
    // dedicated real-time thread with a Windows high-resolution waitable timer, matching
    // PadForge's own approach.
    //
    // User-adjustable (HIDMaestro tab's Submit Rate slider, one of 250/500/750/1000) rather
    // than fixed, trading a small amount of input-timing precision for lower CPU use on the
    // submit thread's spin-wait tail. Read fresh every SubmitThreadProc tick rather than cached
    // once, so a change takes effect on the very next tick without restarting the thread.
    public int SubmitRateHz { get; set; } = 1000;

    private HMContext?    _ctx;
    private HMController? _ctrl;
    private byte          _seqNum;
    private GamepadState  _lastState;
    private CancellationTokenSource? _loopCts;
    private Thread?        _submitThread;
    private ManualResetEvent? _stopEvent;

    // One touchpad-state snapshot shared by both SubmitState and SubmitRawReport for a given
    // tick, matching PadForge's own pattern of sourcing both calls from a single TouchpadState
    // struct per frame - HIDMaestro's writes aren't atomic (per the SDK's own SubmitRawReport
    // doc comment: "without overlaying here, SubmitRawReport clobbers whatever SubmitState
    // wrote a few microseconds earlier"), so both calls must agree on touch data for a given
    // tick or a physical input event landing between two independently-computed reads could
    // make them briefly disagree.
    private readonly record struct TouchState(
        bool Finger0Active, int Finger0X, int Finger0Y,
        bool Finger1Active, int Finger1X, int Finger1Y);

    // Touchpad tracking-ID state, matching PadForge's actual implementation
    // (SonyReportPackers.cs / HMaestroVirtualController.cs): the low 7 bits of each finger's
    // raw tracking-id byte are not a static per-slot value - they're a rolling counter that
    // increments on every touch-down/up transition.
    // PacketCounter: increments once when EITHER finger's active state changes vs the
    // previous tick (matches PadForge's InputManager.Step3 exactly: "tp.Down0 != prev.Down0
    // || tp.Down1 != prev.Down1"). Feeds both HMGamepadState.TouchpadPacketCounter and the
    // raw report's tracking-id low bits (finger0 = counter, finger1 = counter+1).
    private byte _touchPacketCounter;
    private bool _touchFinger0PrevActive;
    private bool _touchFinger1PrevActive;

    // Separate persistent per-finger IDs for the abstract SubmitState path only (PadForge's
    // raw packer doesn't use these - it derives both fingers' tracking bytes from
    // _touchPacketCounter alone). Each increments on its own finger's rising edge and stays
    // at its last value after release (only the Active bit clears) rather than resetting to
    // 0, so a lift-then-new-press reads as a distinct contact - matching PadForge exactly.
    private byte _touchFinger0Id;
    private byte _touchFinger1Id;

    private void UpdateTouchTracking(TouchState touch)
    {
        if (touch.Finger0Active && !_touchFinger0PrevActive) _touchFinger0Id++;
        if (touch.Finger1Active && !_touchFinger1PrevActive) _touchFinger1Id++;

        if (touch.Finger0Active != _touchFinger0PrevActive || touch.Finger1Active != _touchFinger1PrevActive)
            _touchPacketCounter++;

        _touchFinger0PrevActive = touch.Finger0Active;
        _touchFinger1PrevActive = touch.Finger1Active;
    }

    // Pre-allocated axes dict reused every tick - avoids 250 allocs/sec.
    // Sony DualSense axis mapping: X=leftStickX, Y=leftStickY, Z=rightStickX,
    // Rx=leftTrigger, Ry=rightTrigger, Rz=rightStickY (different from Xbox convention).
    private readonly Dictionary<HMAxis, float> _axesScratch = new()
    {
        [HMAxis.X]  = 0.5f,
        [HMAxis.Y]  = 0.5f,
        [HMAxis.Z]  = 0.5f,
        [HMAxis.Rx] = 0f,
        [HMAxis.Ry] = 0f,
        [HMAxis.Rz] = 0.5f,
    };

    public bool   IsRunning    { get; private set; }
    public string? LastError   { get; private set; }

    public bool InvertLeftStickY  { get; set; }
    public bool InvertRightStickY { get; set; }

    // X already gets a mandatory hardware-correction flip below regardless of these - checking
    // one asks for an additional flip on top of that correction. See AnalogAdjustment for the
    // shared logic (also used by PanguEngine's vJoy path) and why the two axes aren't
    // symmetric by default.
    public bool InvertLeftStickX  { get; set; }
    public bool InvertRightStickX { get; set; }

    /// <summary>See AnalogAdjustment - applies after inversion, so invert settings always
    /// target the physical stick/trigger they name regardless of this.</summary>
    public bool SwapSticks   { get; set; }
    public bool SwapTriggers { get; set; }

    /// <summary>Physical digital input -> DualSense Edge output button, editable via the
    /// HIDMaestro tab's mapping table. Read fresh every submit-loop tick (rate set by
    /// SubmitRateHz) with no lock - callers must assign a brand-new dictionary rather than
    /// mutating this one in place, since a reference swap is atomic but concurrent in-place
    /// edits are not.</summary>
    public Dictionary<HmSourceButton, HmOutputButton> ButtonMap { get; set; } = HmMapping.CreateDefault();

    // Fired with (rightMotor, leftMotor) each time Steam/game sends Report 0x02 rumble output.
    public event Action<byte, byte>? RumbleChanged;

    // Fired with (rightEffect, leftEffect) whenever Steam/game sends an output report that
    // actually updates that side's trigger effect (validFlag0 - see OnOutputDecoded). Either
    // side is null when this write didn't touch it - subscribers should leave that side's
    // cached effect alone rather than treating null as "off". See AdaptiveTriggerEffect for
    // what a non-null sample decodes to and PanguEngine.ApplyRumbleOutput for how it gets
    // turned into physical trigger-motor levels.
    public event Action<TriggerEffectSample?, TriggerEffectSample?>? AdaptiveTriggerChanged;

    // Fired with (r, g, b) whenever Steam/game sends an output report that actually updates
    // the lightbar color (validFlag1 bit 0x04 - see OnOutputDecoded and docs/led.md).
    public event Action<byte, byte, byte>? LightbarChanged;

    /// <summary>HIDMaestro.Core.dll's own file version (Major.Minor.Build, e.g. "1.3.17"),
    /// read from the loaded assembly's file rather than hardcoded so it always reflects
    /// whatever lib/HIDMaestro.Core.dll actually ships. Null if the version resource can't be
    /// read for any reason.</summary>
    public static string? LibraryVersion
    {
        get
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(typeof(HMContext).Assembly.Location);
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Checks whether the HIDMaestro driver is already installed in the Windows
    /// DriverStore. Does not require elevation - safe to call from a non-admin process.</summary>
    public static bool CheckIsDriverInstalled()
    {
        try
        {
            using var ctx = new HMContext();
            return ctx.IsDriverInstalled;
        }
        catch
        {
            return false;
        }
    }

    public bool TryStart()
    {
        try
        {
            _ctx = new HMContext();
            _ctx.LoadDefaultProfiles();

            // InstallDriver() requires elevation and is handled separately via the "Install
            // Driver" / "Reinstall Drivers" buttons - it is never called here. If the driver
            // isn't installed yet, surface a clear actionable error instead of failing silently.
            if (!_ctx.IsDriverInstalled)
            {
                LastError = "Driver not installed - use Install Driver on the Status tab or Reinstall Drivers on the HIDMaestro tab.";
                Cleanup();
                return false;
            }

            var profile = _ctx.GetProfile("dualsense-edge")
                ?? throw new InvalidOperationException(
                    "'dualsense-edge' profile not found - HIDMaestro 1.3.17+ required.");

            _ctrl = _ctx.CreateController(profile);
            _ctrl.OutputDecoded += OnOutputDecoded;

            _lastState = GamepadState.Center;

            // Submit an initial frame synchronously, before the loop's first tick, so the
            // device never exists in a state where it hasn't been sent battery/report data.
            // Without this there's a gap (PeriodicTimer's first interval + thread-pool
            // scheduling of the loop task) during which Windows can query battery via
            // GetFeature and cache whatever default the driver answers with - a plausible
            // cause of the low-battery notification never self-correcting later.
            var initialActive = ComputeOutputActive(_lastState);
            var initialTouch = ComputeTouch(initialActive);
            UpdateTouchTracking(initialTouch);
            // _ctrl.SubmitState(BuildHmState(_lastState, initialActive, initialTouch)); // see SubmitThreadProc
            _ctrl.SubmitRawReport(BuildReport(_lastState, initialActive, initialTouch));

            _loopCts   = new CancellationTokenSource();
            _stopEvent = new ManualResetEvent(false);
            _submitThread = new Thread(() => SubmitThreadProc(_loopCts.Token))
            {
                Name       = "PanguBridge.HidMaestroSubmit",
                IsBackground = true,
                Priority   = ThreadPriority.AboveNormal,
            };
            _submitThread.Start();

            IsRunning = true;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            Cleanup();
            return false;
        }
    }

    /// <summary>Called by PanguEngine on every HidReader state-change event.
    /// Stores the latest decoded state so the continuous submit loop picks it up
    /// immediately - no need to wait for the next 8ms tick.</summary>
    public void SubmitState(GamepadState s)
    {
        _lastState = s; // loop reads this on the next tick; volatile read is safe since
                        // GamepadState is a readonly record struct (value-type copy on assign)
    }

    /// <summary>
    /// Runs on a dedicated real-time-priority thread (not the thread pool) for the lifetime
    /// of the session, submitting a fresh frame at SubmitRateHz. Mirrors PadForge's approach:
    /// raise the system timer resolution to 1 ms for this thread's lifetime, sleep the bulk of
    /// each interval on a Windows high-resolution waitable timer (precise to ~1 ms without CPU
    /// burn), then spin-wait the last fraction of a millisecond to close out any
    /// timer-resolution overshoot. The precision of delivery, not just the target rate, is what
    /// matters for input latency here - a thread-pool-scheduled timer at normal priority with
    /// no timeBeginPeriod produces a severe input-lag regression even at the same nominal rate.
    /// </summary>
    private void SubmitThreadProc(CancellationToken ct)
    {
        bool   periodRaised = false;
        IntPtr timerHandle  = IntPtr.Zero;
        try
        {
            periodRaised = NativeTiming.timeBeginPeriod(1) == 0;

            timerHandle = NativeTiming.CreateWaitableTimerExW(
                IntPtr.Zero, null,
                NativeTiming.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                NativeTiming.TIMER_ALL_ACCESS);

            IntPtr[]? waitHandles = null;
            if (timerHandle != IntPtr.Zero && _stopEvent != null)
                waitHandles = new[] { _stopEvent.SafeWaitHandle.DangerousGetHandle(), timerHandle };

            long freq    = Stopwatch.Frequency;
            long nextTick = Stopwatch.GetTimestamp();

            while (!ct.IsCancellationRequested)
            {
                if (_ctrl == null) break;
                // Re-read every tick (not cached once before the loop) so a live change to
                // SubmitRateHz from the UI takes effect on the very next tick, no thread restart.
                long intervalTicks = freq / SubmitRateHz;
                try
                {
                    // Snapshot _lastState ONCE per tick - it's written from a different
                    // thread (SubmitState(GamepadState) below, called from PanguEngine's
                    // HidReader callback) with no lock. Reading the field twice (once per
                    // BuildHmState/BuildReport call) risked the two calls seeing different
                    // states if an input event landed between them, which combined with
                    // HIDMaestro's non-atomic SubmitState/SubmitRawReport writes (confirmed
                    // via the SDK's own code comment: "without overlaying here, SubmitRawReport
                    // clobbers whatever SubmitState wrote a few microseconds earlier") could
                    // produce a torn frame. One snapshot + one shared TouchState (below)
                    // guarantees both calls always agree - matching PadForge's pattern of
                    // sourcing both submit calls from a single TouchpadState struct per tick.
                    var state = _lastState;
                    var active = ComputeOutputActive(state);
                    var touch = ComputeTouch(active);
                    UpdateTouchTracking(touch);

                    // Only SubmitRawReport is used, not SubmitState - HIDMaestro's SubmitState
                    // encoder has an internal touchpad-region bug: it defaults inactive touch
                    // state to (0,0)+active independent of what HMGamepadState says, which
                    // briefly wins the write race against SubmitRawReport and produces phantom
                    // touchpad taps at the origin. BuildHmState is kept for reference but not
                    // called.
                    _ctrl.SubmitRawReport(BuildReport(state, active, touch));
                }
                catch { /* best-effort - one dropped frame isn't worth surfacing */ }

                // Schedule against an absolute timeline (nextTick += interval) rather than
                // "sleep(interval) after work completes" - the latter accumulates drift equal
                // to however long each tick's work took. If we're already past the deadline
                // (a long GC pause, system hiccup), resync to now instead of trying to catch
                // up with a burst of back-to-back submissions.
                nextTick += intervalTicks;
                long now = Stopwatch.GetTimestamp();
                if (now >= nextTick) { nextTick = now; continue; }

                double remainingMs = (nextTick - now) * 1000.0 / freq;

                if (waitHandles != null && remainingMs > 1.5)
                {
                    long dueTime100ns = -(long)((remainingMs - 1.0) * 10_000); // negative = relative
                    if (NativeTiming.SetWaitableTimer(timerHandle, ref dueTime100ns, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        uint waitResult = NativeTiming.WaitForMultipleObjects(
                            (uint)waitHandles.Length, waitHandles, false, NativeTiming.INFINITE);
                        if (waitResult == NativeTiming.WAIT_OBJECT_0) return; // stop signaled
                    }
                }
                else if (waitHandles == null && remainingMs > 1.5)
                {
                    // No high-resolution timer available (pre-1803 Windows) - fall back to
                    // Thread.Sleep, still tightened to ~1 ms by timeBeginPeriod(1) above.
                    if (ct.WaitHandle.WaitOne((int)(remainingMs - 1.0))) return;
                }

                // Final close-out: spin the last sub-millisecond to cover both HR-timer
                // overshoot and (when no HR timer exists) the whole remaining wait.
                while (Stopwatch.GetTimestamp() < nextTick)
                {
                    if (ct.IsCancellationRequested) return;
                    Thread.SpinWait(50);
                }
            }
        }
        finally
        {
            if (timerHandle != IntPtr.Zero) NativeTiming.CloseHandle(timerHandle);
            if (periodRaised) NativeTiming.timeEndPeriod(1);
        }
    }

    /// <summary>Win32 interop for precise submit-loop timing - see SubmitThreadProc.</summary>
    private static class NativeTiming
    {
        public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        public const uint TIMER_ALL_ACCESS = 0x1F0003;
        public const uint WAIT_OBJECT_0    = 0x00000000;
        public const uint INFINITE         = 0xFFFFFFFF;

        [DllImport("winmm.dll", ExactSpelling = true)]
        public static extern uint timeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", ExactSpelling = true)]
        public static extern uint timeEndPeriod(uint uMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWaitableTimerExW(
            IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWaitableTimer(
            IntPtr hTimer, ref long pDueTime, int lPeriod,
            IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForMultipleObjects(
            uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }

    // GamepadState field for a given remappable digital input - the read side of ButtonMap.
    private static bool GetSourceState(GamepadState s, HmSourceButton src) => src switch
    {
        HmSourceButton.DPadUp => s.DPadUp,
        HmSourceButton.DPadDown => s.DPadDown,
        HmSourceButton.DPadLeft => s.DPadLeft,
        HmSourceButton.DPadRight => s.DPadRight,
        HmSourceButton.A => s.A,
        HmSourceButton.B => s.B,
        HmSourceButton.X => s.X,
        HmSourceButton.Y => s.Y,
        HmSourceButton.LeftShoulder => s.LeftShoulder,
        HmSourceButton.RightShoulder => s.RightShoulder,
        HmSourceButton.LeftThumbClick => s.LeftThumbClick,
        HmSourceButton.RightThumbClick => s.RightThumbClick,
        HmSourceButton.Start => s.Start,
        HmSourceButton.Back => s.Back,
        HmSourceButton.Ai => s.Ai,
        HmSourceButton.Fn => s.Fn,
        HmSourceButton.Profile => s.Profile,
        HmSourceButton.M1 => s.M1,
        HmSourceButton.M2 => s.M2,
        HmSourceButton.M3 => s.M3,
        HmSourceButton.M4 => s.M4,
        HmSourceButton.Lm => s.Lm,
        HmSourceButton.Rm => s.Rm,
        _ => false,
    };

    /// <summary>For each DualSense Edge output, true if any physical input currently mapped to
    /// it (via <see cref="ButtonMap"/>) is held. Multiple inputs can share one output - it
    /// fires if any of them are pressed. Indexed by (int)HmOutputButton.</summary>
    private bool[] ComputeOutputActive(GamepadState s)
    {
        var active = new bool[Enum.GetValues<HmOutputButton>().Length];
        foreach (var (src, dst) in ButtonMap)
        {
            if (dst == HmOutputButton.None) continue;
            if (GetSourceState(s, src)) active[(int)dst] = true;
        }
        return active;
    }

    private byte[] BuildReport(GamepadState s, bool[] active, TouchState touch)
    {
        // 63 bytes of data (no report ID prefix - SubmitRawReport strips/prepends it).
        // All array indices are 0-based data offsets.  The profile JSON's "byte:N" fields
        // are 1-based (byte 1 = data[0]), so data index = JSON byte - 1.
        var r = new byte[63];

        // Sticks (data[0-3]): 0-255, center 128. Pangu raw X is inverted (high=left, low=right)
        // - AnalogAdjustment flips it for DualSense convention and applies invert/swap.
        var (leftX, leftY, rightX, rightY) = AnalogAdjustment.ComputeSticks(
            s, InvertLeftStickX, InvertLeftStickY, InvertRightStickX, InvertRightStickY, SwapSticks);
        var (leftTrigger, rightTrigger) = AnalogAdjustment.ComputeTriggers(s, SwapTriggers);

        r[0] = leftX;
        r[1] = leftY;
        r[2] = rightX;
        r[3] = rightY;

        // Triggers (data[4-5]): 0-255, direct pass-through
        r[4] = leftTrigger;
        r[5] = rightTrigger;

        // Sequence number (data[6]): rolling, lets the driver detect dropped frames
        r[6] = _seqNum++;

        // data[7] = hat (bits 0-3) + face buttons (bits 4-7)
        // Hat: 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW, 8=neutral
        // Face (per buttonMap [1,2,0,3,...]): bit4=X(Square), bit5=A(Cross), bit6=B(Circle), bit7=Y(Triangle)
        byte hat = ComputeHat(
            active[(int)HmOutputButton.DPadUp], active[(int)HmOutputButton.DPadDown],
            active[(int)HmOutputButton.DPadLeft], active[(int)HmOutputButton.DPadRight]);
        byte face = 0;
        if (active[(int)HmOutputButton.X]) face |= 0x10;
        if (active[(int)HmOutputButton.A]) face |= 0x20;
        if (active[(int)HmOutputButton.B]) face |= 0x40;
        if (active[(int)HmOutputButton.Y]) face |= 0x80;
        r[7] = (byte)(hat | face);

        // data[8]: LB/RB, LT/RT digital, Back/Start, LS/RS
        byte b8 = 0;
        if (active[(int)HmOutputButton.LeftShoulder])    b8 |= 0x01;
        if (active[(int)HmOutputButton.RightShoulder])   b8 |= 0x02;
        if (leftTrigger  > 127)                          b8 |= 0x04; // LT digital threshold
        if (rightTrigger > 127)                          b8 |= 0x08; // RT digital threshold
        if (active[(int)HmOutputButton.Back])            b8 |= 0x10;
        if (active[(int)HmOutputButton.Start])           b8 |= 0x20;
        if (active[(int)HmOutputButton.LeftThumbClick])  b8 |= 0x40;
        if (active[(int)HmOutputButton.RightThumbClick]) b8 |= 0x80;
        r[8] = b8;

        // data[9] = Guide/Touchpad/Mute + paddle/Fn bits.
        // Bit assignments confirmed empirically against Steam's controller config display:
        //   bit3 (0x08) = unrecognized by Steam - unused
        //   bit4 (0x10) = PS Function N (left Fn button, under left stick)
        //   bit5 (0x20) = PS Function (right Fn button, under right stick)
        //   bit6 (0x40) = PS LB (left back paddle) ✓ verified
        //   bit7 (0x80) = PS RB (right back paddle) - inferred as the only remaining bit
        byte b9 = 0;
        if (active[(int)HmOutputButton.Guide])        b9 |= 0x01; // Guide / PS button
        if (active[(int)HmOutputButton.TouchpadClick]) b9 |= 0x02; // Touchpad physical click
        if (active[(int)HmOutputButton.Mute])         b9 |= 0x04; // Mute button
        if (active[(int)HmOutputButton.RightFnButton]) b9 |= 0x20; // PS Function (right Fn)
        if (active[(int)HmOutputButton.RightPaddle])  b9 |= 0x80; // PS RB (right back paddle)
        if (active[(int)HmOutputButton.LeftFnButton]) b9 |= 0x10; // PS Function N (left Fn)
        if (active[(int)HmOutputButton.LeftPaddle])   b9 |= 0x40; // PS LB (left back paddle) ✓
        r[9] = b9;

        // Battery status (data[52]): bits 0-3 = level 0-10 (10=100%). Bits 4-7 are not
        // independent flags - per the Linux hid-playstation.c kernel driver (the authoritative
        // reference for this protocol), they're a single 4-bit enum: 0x0=discharging,
        // 0x1=charging, 0x2=full, 0xA/0xB=error, 0xF=unknown. OR-ing charging and full bits
        // together produces an invalid combined state. Always-100% + USB means "full", not
        // "still charging".
        r[52] = 0x0A | 0x20; // level=10 (100%), upper nibble 0x2 = Full

        // Touchpad fingers (data[32-35] = finger0, data[36-39] = finger1) - sourced from the
        // same TouchState the caller passed to BuildHmState this tick, so SubmitState and
        // SubmitRawReport can never disagree about touch data (see TouchState doc comment).
        // Tracking-id low bits are not a static per-slot value - matching PadForge's actual raw
        // packer (SonyReportPackers.cs), the low 7 bits of each finger's tracking byte are
        // _touchPacketCounter (finger0) and _touchPacketCounter+1 (finger1), a counter that
        // rolls forward on every touch transition.
        EncodeFinger(r, 32, touch.Finger0Active, touch.Finger0X, touch.Finger0Y, _touchPacketCounter);
        EncodeFinger(r, 36, touch.Finger1Active, touch.Finger1X, touch.Finger1Y, (byte)(_touchPacketCounter + 1));

        // data[48-51]: unnamed Edge-specific status region immediately after the touchpad
        // fingers - not covered by any field in the dualsense-edge profile's extendedReport,
        // but the profile JSON declares explicit inputDefaults for it: byte49=128, byte50=0,
        // byte51=0, byte52=0 (1-based; -1 for our 0-based data[]). data[48] defaults to 128
        // (0x80 - the same "inactive" sentinel used throughout the touchpad encoding). PadForge's
        // own SDK source names data[48] specifically as "activeProfile" and warns that a raw
        // report which doesn't set it "clobbers whatever SubmitState wrote a few microseconds
        // earlier" - sitting directly adjacent to the touchpad region, an invalid profile-state
        // byte here is a plausible cause of touchpad misreads.
        r[48] = 128;
        r[49] = 0;
        r[50] = 0;
        r[51] = 0;

        return r;
    }

    private static byte ComputeHat(bool up, bool down, bool left, bool right)
    {
        if (up   && right) return 1; // NE
        if (down && right) return 3; // SE
        if (down && left)  return 5; // SW
        if (up   && left)  return 7; // NW
        if (up)            return 0; // N
        if (right)         return 2; // E
        if (down)          return 4; // S
        if (left)          return 6; // W
        return 8;                    // neutral
    }

    private static void EncodeFinger(byte[] r, int offset, bool active, int x, int y, byte id)
    {
        r[offset + 0] = (byte)((active ? 0x00 : 0x80) | (id & 0x7F));
        r[offset + 1] = (byte)(x & 0xFF);
        r[offset + 2] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        r[offset + 3] = (byte)((y >> 4) & 0xFF);
    }

    // LM = left-half touch, RM = right-half touch. FN (touchpad click) with neither held
    // encodes a dummy center finger so the report reflects realistic hardware state (a
    // click always has a finger present). Shared by BuildHmState and BuildReport so both
    // submit calls always agree on touch data for a given tick - see TouchState above.
    //
    // Inactive-finger X/Y is TpCenterX/TpCenterY, not (0,0) - HIDMaestro's SubmitState
    // touchpad encoder has an internal bug where Active=false + X=0 + Y=0 can render as a
    // permanently active touch at (0,0) if that encoder ever wins a write race against
    // SubmitRawReport (see SubmitThreadProc). A non-zero resting position costs nothing since
    // a correct consumer ignores X/Y whenever the Active bit is clear.
    private TouchState ComputeTouch(bool[] active)
    {
        bool lm = active[(int)HmOutputButton.TouchpadLeftTouch];
        bool rm = active[(int)HmOutputButton.TouchpadRightTouch];
        bool click = active[(int)HmOutputButton.TouchpadClick];

        if (lm && rm) return new TouchState(true, TpLeftX,   TpCenterY, true, TpRightX, TpCenterY);
        if (lm)       return new TouchState(true, TpLeftX,   TpCenterY, false, TpCenterX, TpCenterY);
        if (rm)       return new TouchState(true, TpRightX,  TpCenterY, false, TpCenterX, TpCenterY);
        if (click)    return new TouchState(true, TpCenterX, TpCenterY, false, TpCenterX, TpCenterY);
        return new TouchState(false, TpCenterX, TpCenterY, false, TpCenterX, TpCenterY);
    }

    private HMGamepadState BuildHmState(GamepadState s, bool[] active, TouchState touch)
    {
        var (leftX, leftY, rightX, rightY) = AnalogAdjustment.ComputeSticks(
            s, InvertLeftStickX, InvertLeftStickY, InvertRightStickX, InvertRightStickY, SwapSticks);
        var (leftTrigger, rightTrigger) = AnalogAdjustment.ComputeTriggers(s, SwapTriggers);

        _axesScratch[HMAxis.X]  = leftX  / 255f;
        _axesScratch[HMAxis.Y]  = leftY  / 255f;
        _axesScratch[HMAxis.Z]  = rightX / 255f;
        _axesScratch[HMAxis.Rz] = rightY / 255f;
        _axesScratch[HMAxis.Rx] = leftTrigger  / 255f;
        _axesScratch[HMAxis.Ry] = rightTrigger / 255f;

        HMButton buttons = HMButton.None;
        if (active[(int)HmOutputButton.A])               buttons |= HMButton.A;
        if (active[(int)HmOutputButton.B])               buttons |= HMButton.B;
        if (active[(int)HmOutputButton.X])               buttons |= HMButton.X;
        if (active[(int)HmOutputButton.Y])               buttons |= HMButton.Y;
        if (active[(int)HmOutputButton.LeftShoulder])    buttons |= HMButton.LeftBumper;
        if (active[(int)HmOutputButton.RightShoulder])   buttons |= HMButton.RightBumper;
        if (active[(int)HmOutputButton.Back])            buttons |= HMButton.Back;
        if (active[(int)HmOutputButton.Start])           buttons |= HMButton.Start;
        if (active[(int)HmOutputButton.LeftThumbClick])  buttons |= HMButton.LeftStick;
        if (active[(int)HmOutputButton.RightThumbClick]) buttons |= HMButton.RightStick;
        if (active[(int)HmOutputButton.Guide])           buttons |= HMButton.Guide;
        if (active[(int)HmOutputButton.TouchpadClick])   buttons |= HMButton.Touchpad;

        HMHat hat = (active[(int)HmOutputButton.DPadUp], active[(int)HmOutputButton.DPadDown],
                     active[(int)HmOutputButton.DPadLeft], active[(int)HmOutputButton.DPadRight]) switch
        {
            (true,  false, false, true)  => HMHat.NorthEast,
            (true,  false, true,  false) => HMHat.NorthWest,
            (false, true,  false, true)  => HMHat.SouthEast,
            (false, true,  true,  false) => HMHat.SouthWest,
            (true,  false, false, false) => HMHat.North,
            (false, false, false, true)  => HMHat.East,
            (false, true,  false, false) => HMHat.South,
            (false, false, true,  false) => HMHat.West,
            _                            => HMHat.None,
        };

        return new HMGamepadState
        {
            Axes    = _axesScratch,
            Buttons = buttons,
            Hat     = hat,
            // Mirrors r[52] in BuildReport: Full, not Charging+Full simultaneously - the
            // real protocol's charging-status nibble is a single enum (0=discharging,
            // 1=charging, 2=full), not independent flags. Setting both likely produces the
            // same invalid combined value through HIDMaestro's own bitfield encoder for
            // this field, same as our raw byte was doing.
            BatteryLevel          = 10,
            BatteryFull           = true,
            BatteryCharging       = false,
            TouchpadFinger0Active = touch.Finger0Active,
            TouchpadFinger0X      = (ushort)touch.Finger0X,
            TouchpadFinger0Y      = (ushort)touch.Finger0Y,
            TouchpadFinger0Id     = (byte)(_touchFinger0Id & 0x7F),
            TouchpadFinger1Active = touch.Finger1Active,
            TouchpadFinger1X      = (ushort)touch.Finger1X,
            TouchpadFinger1Y      = (ushort)touch.Finger1Y,
            TouchpadFinger1Id     = (byte)(_touchFinger1Id & 0x7F),
            // Matches PadForge, which maintains and sends this exact field the same way.
            TouchpadPacketCounter = _touchPacketCounter,
        };
    }

    private void OnOutputDecoded(object? sender, HMOutputDecodedEventArgs e)
    {
        if (e.Fields.TryGetValue("leftMotor",  out var lObj) && lObj  is byte leftMotor
         && e.Fields.TryGetValue("rightMotor", out var rObj) && rObj is byte rightMotor)
        {
            RumbleChanged?.Invoke(rightMotor, leftMotor);
        }

        // The DualSense output report is a full-buffer replace, not a delta - every write
        // carries trigger-effect bytes whether or not this particular write actually means to
        // change them. validFlag0 bits 0x04/0x08 are the protocol's own "this write actually
        // touches right/left trigger effect" signal, per dualsensectl's source (its
        // command_trigger sets these bits; its vibration command doesn't). Without checking
        // this, a rumble-only write whose trigger-effect bytes happen to be stale/zeroed would
        // look identical to the game explicitly turning the effect off.
        if (e.Fields.TryGetValue("validFlag0", out var vObj) && vObj is byte validFlag0
         && e.Fields.TryGetValue("rightTriggerEffect", out var rEffObj) && rEffObj is byte[] rEffBytes
         && e.Fields.TryGetValue("leftTriggerEffect",  out var lEffObj) && lEffObj is byte[] lEffBytes)
        {
            bool rightValid = (validFlag0 & 0x04) != 0;
            bool leftValid  = (validFlag0 & 0x08) != 0;

            if (rightValid || leftValid)
            {
                AdaptiveTriggerChanged?.Invoke(
                    rightValid ? TriggerEffectSample.Decode(rEffBytes) : null,
                    leftValid  ? TriggerEffectSample.Decode(lEffBytes) : null);
            }
        }

        // Same full-buffer-replace reasoning as the trigger effect above, but gated on
        // validFlag1 bit 0x04 (DS_OUTPUT_VALID_FLAG1_LIGHTBAR_CONTROL_ENABLE per dualsensectl) -
        // a different byte (byte 2 of the report, not byte 1) from the trigger-effect valid
        // flags. "rgb24" fields decode as byte[3] in (R, G, B) order per the HIDMaestro SDK.
        if (e.Fields.TryGetValue("validFlag1", out var v1Obj) && v1Obj is byte validFlag1
         && (validFlag1 & 0x04) != 0
         && e.Fields.TryGetValue("lightbar", out var lbObj) && lbObj is byte[] lbBytes && lbBytes.Length >= 3)
        {
            LightbarChanged?.Invoke(lbBytes[0], lbBytes[1], lbBytes[2]);
        }
    }

    private void Cleanup()
    {
        _loopCts?.Cancel();
        _stopEvent?.Set(); // wakes SubmitThreadProc immediately if it's blocked in WaitForMultipleObjects
        _submitThread?.Join(TimeSpan.FromSeconds(1));
        _submitThread = null;
        _stopEvent?.Dispose();
        _stopEvent = null;
        _loopCts?.Dispose();
        _loopCts = null;

        if (_ctrl != null)
        {
            _ctrl.OutputDecoded -= OnOutputDecoded;
            _ctrl.Dispose();
            _ctrl = null;
        }
        _ctx?.Dispose();
        _ctx = null;
        IsRunning = false;
    }

    public void Stop() => Cleanup();

    public void Dispose() => Cleanup();
}
