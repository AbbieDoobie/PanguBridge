using System.IO;
using PanguBridge.Controllers;
using PanguBridge.Mapping;

namespace PanguBridge;

/// <summary>
/// Wires HidReader's full gamepad state into the active virtual output: HIDMaestro DualSense
/// Edge (preferred, gives real proportional rumble) or vJoy (fallback when HIDMaestro is not
/// installed/available, no rumble support). Only one virtual device is ever live at a time to
/// avoid Steam seeing two controllers and splitting them into separate player slots.
/// Single-process equivalent of the old PanguService - see docs/architecture.md. Interface 4
/// carries every standard control alongside the extra buttons, so there is no longer a
/// separate XInput-sourced reader (see docs/hid-report-format.md).
/// </summary>
public sealed class PanguEngine : IDisposable
{
    public HidReader       HidReader       { get; } = new();
    public HidMaestroOutput HidMaestroOutput { get; }
    public VJoyOutput      VJoyOutput      { get; }
    public ButtonMapper    ButtonMapper     { get; }
    public MappingProfile  Profile          { get; private set; }
    public AudioAutoHapticsCapture AudioAutoHaptics { get; } = new();

    /// <summary>True when HIDMaestro started successfully and is the active output.
    /// False = vJoy is the active output (or neither is ready yet).</summary>
    public bool UsingHidMaestro { get; private set; }

    /// <summary>True while CreateController() is running on a background thread -
    /// the Status tab shows a yellow "working" indicator during this window.</summary>
    public bool IsHidMaestroStarting { get; private set; }

    private const int VJoyRetryDelayMs = 2000;

    private readonly AppSettings _settings;
    private CancellationTokenSource? _vjoyRetryCts;

    // Rumble routing state. Grip and trigger are two separate physical motor subsystems - see
    // RumbleMode's doc comment in AppSettings.cs, and HidReader.TrySendMotors for why they're
    // sent as one combined report rather than two independent ones.
    private readonly object _rumbleLock = new();
    private byte _lastRightMotor;
    private byte _lastLeftMotor;
    private int  _lastSentGripLeft = -1;
    private int  _lastSentGripRight = -1;
    private int  _lastSentTrigLeft = -1;
    private int  _lastSentTrigRight = -1;

    // Last time TrySendMotors was actually called with a nonzero motor active - see the
    // periodic-refresh comment at the send site. DateTime.MinValue forces an immediate first
    // send rather than waiting out the refresh interval.
    private DateTime _lastMotorSendTime = DateTime.MinValue;
    private static readonly TimeSpan MotorRefreshInterval = TimeSpan.FromMilliseconds(100);

    // Latest decoded adaptive-trigger effect from Steam/the game (see
    // HidMaestroOutput.AdaptiveTriggerChanged), read continuously by ApplyRumbleOutput
    // whenever AppSettings.AdaptiveTriggerSimulation is on. Off until the first output report
    // carrying trigger-effect bytes arrives, matching TriggerEffectSample's own Off default.
    private TriggerEffectSample _lastLeftTriggerEffect = TriggerEffectSample.Off;
    private TriggerEffectSample _lastRightTriggerEffect = TriggerEffectSample.Off;

    // Only exists while RumbleMode is one of the two "if pulled" modes - see
    // EnsureRumbleGateLoopRunning. Reacts to physical trigger pull/release between Steam
    // rumble packets without adding any latency to input capture: it never touches the HID
    // read thread or HIDMaestro's real-time submit thread, and only ever contends with
    // HidReader's existing write lock (already shared by the keepalive loop and test buttons).
    private Thread? _rumbleGateThread;
    private CancellationTokenSource? _rumbleGateCts;

    /// <summary>Fired on any connection/acquisition state change, for UI refresh.</summary>
    public event Action? StatusChanged;

    /// <summary>True between Start() and Stop(). While false, HidReader/VJoyOutput
    /// not being connected is expected, not an error - see StatusView's grey-vs-red logic.</summary>
    public bool IsRunning { get; private set; }

    public PanguEngine(MappingProfile profile, AppSettings settings)
    {
        Profile = profile;
        _settings = settings;
        HidMaestroOutput = new HidMaestroOutput
        {
            InvertLeftStickX  = settings.InvertLeftStickX,
            InvertLeftStickY  = settings.InvertLeftStickY,
            InvertRightStickX = settings.InvertRightStickX,
            InvertRightStickY = settings.InvertRightStickY,
            SwapSticks        = settings.SwapSticks,
            SwapTriggers      = settings.SwapTriggers,
            ButtonMap         = settings.HmButtonMap,
            SubmitRateHz      = settings.HmSubmitRateHz,
        };
        HidMaestroOutput.RumbleChanged += OnHidMaestroRumble;
        HidMaestroOutput.AdaptiveTriggerChanged += OnAdaptiveTriggerChanged;
        HidMaestroOutput.LightbarChanged += OnLightbarChanged;
        VJoyOutput   = new VJoyOutput(settings.VJoyDeviceId);
        ButtonMapper = new ButtonMapper(profile);

        HidReader.StateChanged      += OnHidStateChanged;
        HidReader.ConnectionChanged += OnHidConnectionChanged;
    }

    /// <returns>True if the chosen backend started (or is starting) successfully.</returns>
    public bool Start()
    {
        IsRunning = true;

        if (_settings.Backend == VirtualBackend.HidMaestro)
        {
            HidReader.Start();

            if (_settings.HmConnectMode == HmConnectMode.OnStart)
                _ = StartHidMaestroAsync();
            // OnControllerConnect: deferred - OnHidConnectionChanged triggers the start.

            // Only meaningful on HIDMaestro - vJoy has no rumble output at all, so nothing
            // would ever consume the derived signal.
            RefreshAudioAutoHapticsCapture();

            StatusChanged?.Invoke();
            return true;
        }

        // vJoy backend.
        bool vjoyOk = VJoyOutput.TryAcquire();
        HidReader.Start();

        if (!vjoyOk)
        {
            _vjoyRetryCts = new CancellationTokenSource();
            _ = Task.Run(() => RetryVJoyAcquireLoopAsync(_vjoyRetryCts.Token));
        }

        StatusChanged?.Invoke();
        return vjoyOk;
    }

    public void Stop()
    {
        IsRunning = false;

        _vjoyRetryCts?.Cancel();
        _vjoyRetryCts = null;

        StopRumbleGateLoop();
        AudioAutoHaptics.Stop();

        HidReader.Stop();

        HidMaestroOutput.Stop();
        UsingHidMaestro = false;
        IsHidMaestroStarting = false;

        VJoyOutput.Release();

        StatusChanged?.Invoke();
    }

    private async Task RetryVJoyAcquireLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && !VJoyOutput.IsAcquired)
        {
            try
            {
                await Task.Delay(VJoyRetryDelayMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;

            VJoyOutput.TryAcquire();

            StatusChanged?.Invoke();
        }
    }

    // HIDMaestro path: real proportional rumble - Steam sends the actual left/right motor
    // magnitudes directly in the DualSense output report. Route them per RumbleMode instead of
    // forwarding straight to grip: see ApplyRumbleOutput for the per-mode logic.
    private void OnHidMaestroRumble(byte rightMotor, byte leftMotor)
    {
        _lastRightMotor = rightMotor;
        _lastLeftMotor = leftMotor;

        if (NeedsGateLoop())
        {
            // These modes need continuous reevaluation against physical trigger pull state,
            // not just against new Steam packets - the gate loop (already running or started
            // here) picks up these new motor values on its next tick instead of us applying
            // them synchronously on this thread.
            EnsureRumbleGateLoopRunning();
        }
        else
        {
            ApplyRumbleOutput();
        }
    }

    // Adaptive-trigger effects arrive independently of rumble packets (a game can send one
    // without the other) and, unlike grip rumble, need continuous reevaluation against the
    // physical trigger's live pull position rather than just against new packets - same
    // reasoning as the "if pulled" RumbleModes, so this reuses the same gate loop. Either
    // parameter is null when HidMaestroOutput determined this particular write didn't actually
    // touch that side's effect (validFlag0) - leave the cached value alone in that case rather
    // than overwriting a live effect with stale/zeroed bytes from an unrelated write.
    private void OnAdaptiveTriggerChanged(TriggerEffectSample? rightEffect, TriggerEffectSample? leftEffect)
    {
        if (rightEffect.HasValue) _lastRightTriggerEffect = rightEffect.Value;
        if (leftEffect.HasValue) _lastLeftTriggerEffect = leftEffect.Value;

        if (NeedsGateLoop())
            EnsureRumbleGateLoopRunning();
        else
            ApplyRumbleOutput();
    }

    // Unlike adaptive-trigger effects, lightbar color doesn't depend on any live physical
    // state - it's a simple "set and hold" value, so this just forwards it straight through.
    // See HidReader.TrySetLedColor and docs/led.md for why this also fixes trigger-config
    // sends from clobbering the LED color to a fixed wrong one.
    private void OnLightbarChanged(byte r, byte g, byte b) => HidReader.TrySetLedColor(r, g, b);

    private static bool IsGatedRumbleMode(RumbleMode mode) =>
        mode is RumbleMode.GripAndTriggerIfPulled or RumbleMode.GripOrTriggerIfPulled;

    private bool NeedsGateLoop() =>
        IsGatedRumbleMode(_settings.RumbleMode) || _settings.AdaptiveTriggerSimulation
            || _settings.AudioAutoHapticsEnabled;

    /// <summary>Called by the Options UI right after changing RumbleMode or an intensity cap,
    /// so the change is felt immediately instead of waiting for the next Steam rumble packet.</summary>
    public void RefreshRumbleSettings()
    {
        if (!IsRunning) return;

        if (NeedsGateLoop())
            EnsureRumbleGateLoopRunning();
        else
            StopRumbleGateLoop();

        ApplyRumbleOutput();
    }

    /// <summary>Called by the Options UI right after toggling Audio Auto Haptics on/off or
    /// changing its capture device - (re)starts or stops the actual WASAPI loopback capture.
    /// Only takes effect on the HIDMaestro backend; vJoy has no rumble output to drive.</summary>
    public void RefreshAudioAutoHapticsCapture()
    {
        if (NeedsGateLoop())
            EnsureRumbleGateLoopRunning();
        else
            StopRumbleGateLoop();

        if (IsRunning && _settings.Backend == VirtualBackend.HidMaestro && _settings.AudioAutoHapticsEnabled)
        {
            AudioAutoHaptics.CutoffHz        = _settings.AudioAutoHapticsCutoffHz;
            AudioAutoHaptics.AttackMs        = _settings.AudioAutoHapticsAttackMs;
            AudioAutoHaptics.ReleaseMs       = _settings.AudioAutoHapticsReleaseMs;
            AudioAutoHaptics.IntensityBoost  = _settings.AudioAutoHapticsIntensityBoost;
            AudioAutoHaptics.IncludeLfe      = _settings.AudioAutoHapticsIncludeLfe;
            AudioAutoHaptics.Start(_settings.AudioAutoHapticsDeviceId);
        }
        else
        {
            AudioAutoHaptics.Stop();
        }

        ApplyRumbleOutput();
    }

    /// <summary>Called by the Options UI's Advanced controls (cutoff/attack/release/intensity
    /// boost/include-LFE) - updates the live capture's DSP settings in place rather than
    /// restarting capture, so dragging a slider doesn't glitch the audio stream on every tick.
    /// Noise Floor sliders don't call this - they're read fresh from AppSettings in
    /// ApplyRumbleOutput instead, since one shared capture output feeds three differently-gated
    /// motor groups.</summary>
    public void RefreshAudioAutoHapticsTuning()
    {
        AudioAutoHaptics.CutoffHz       = _settings.AudioAutoHapticsCutoffHz;
        AudioAutoHaptics.AttackMs       = _settings.AudioAutoHapticsAttackMs;
        AudioAutoHaptics.ReleaseMs      = _settings.AudioAutoHapticsReleaseMs;
        AudioAutoHaptics.IntensityBoost = _settings.AudioAutoHapticsIntensityBoost;
        AudioAutoHaptics.IncludeLfe     = _settings.AudioAutoHapticsIncludeLfe;
    }

    /// <summary>Called by the HIDMaestro tab's Submit Rate slider - updates the live submit
    /// thread's rate in place, no restart needed since SubmitThreadProc re-reads the property
    /// every tick.</summary>
    public void RefreshHmSubmitRate()
    {
        HidMaestroOutput.SubmitRateHz = _settings.HmSubmitRateHz;
    }

    private void EnsureRumbleGateLoopRunning()
    {
        if (_rumbleGateThread != null) return;

        var cts = new CancellationTokenSource();
        _rumbleGateCts = cts;
        _rumbleGateThread = new Thread(() => RumbleGateLoop(cts.Token))
        {
            Name = "PanguBridge.RumbleGate",
            IsBackground = true,
        };
        _rumbleGateThread.Start();
    }

    private void StopRumbleGateLoop()
    {
        _rumbleGateCts?.Cancel();
        _rumbleGateCts = null;
        _rumbleGateThread = null; // the loop's own thread exits on its own via the token check
    }

    // ~60 Hz: fast enough that a trigger pull/release (or an adaptive-trigger effect's force
    // curve) feels immediate, far below anything that would meaningfully load a CPU core.
    // Self-terminates the moment neither a gated RumbleMode nor adaptive-trigger simulation
    // is active, so it never runs when it isn't needed.
    private void RumbleGateLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!NeedsGateLoop()) break;

            ApplyRumbleOutput();

            try { Thread.Sleep(16); }
            catch { break; }
        }

        _rumbleGateThread = null;
        _rumbleGateCts = null;
    }

    /// <summary>
    /// Computes grip/trigger output for the current RumbleMode + intensity caps from the last
    /// motor values Steam sent, and the physical trigger pull state read live from
    /// HidReader.CurrentState. Only sends when the computed output actually changed, so the
    /// ~60 Hz gate loop doesn't spam redundant HID writes every tick.
    /// </summary>
    private void ApplyRumbleOutput()
    {
        lock (_rumbleLock)
        {
            byte rawLeftGrip = _lastLeftMotor;
            byte rawRightGrip = _lastRightMotor;

            var state = HidReader.CurrentState;
            bool leftTriggerPulled = state.LeftTrigger > 0;
            bool rightTriggerPulled = state.RightTrigger > 0;

            byte gripLeft = 0, gripRight = 0;
            int trigLeft = 0, trigRight = 0;

            // Whether RumbleMode makes a given motor eligible to vibrate at all right now - not
            // just a static per-mode category, since the "IfPulled" modes gate trigger
            // eligibility on live pull state too. Audio Auto Haptics (below) only ever touches
            // a motor group when its flag here is true, so it can never turn on a motor
            // RumbleMode currently excludes.
            bool gripLeftActive = false, gripRightActive = false, trigLeftActive = false, trigRightActive = false;

            switch (_settings.RumbleMode)
            {
                case RumbleMode.GripOnly:
                    gripLeft = rawLeftGrip;
                    gripRight = rawRightGrip;
                    gripLeftActive = gripRightActive = true;
                    break;

                case RumbleMode.TriggerOnly:
                    trigLeft = ScaleToTriggerLevel(rawLeftGrip);
                    trigRight = ScaleToTriggerLevel(rawRightGrip);
                    trigLeftActive = trigRightActive = true;
                    break;

                case RumbleMode.GripAndTrigger:
                    gripLeft = rawLeftGrip;
                    gripRight = rawRightGrip;
                    trigLeft = ScaleToTriggerLevel(rawLeftGrip);
                    trigRight = ScaleToTriggerLevel(rawRightGrip);
                    gripLeftActive = gripRightActive = trigLeftActive = trigRightActive = true;
                    break;

                case RumbleMode.GripAndTriggerIfPulled:
                    gripLeft = rawLeftGrip;
                    gripRight = rawRightGrip;
                    gripLeftActive = gripRightActive = true;
                    trigLeft = leftTriggerPulled ? ScaleToTriggerLevel(rawLeftGrip) : 0;
                    trigRight = rightTriggerPulled ? ScaleToTriggerLevel(rawRightGrip) : 0;
                    trigLeftActive = leftTriggerPulled;
                    trigRightActive = rightTriggerPulled;
                    break;

                case RumbleMode.GripOrTriggerIfPulled:
                    if (leftTriggerPulled) { trigLeft = ScaleToTriggerLevel(rawLeftGrip); trigLeftActive = true; }
                    else { gripLeft = rawLeftGrip; gripLeftActive = true; }
                    if (rightTriggerPulled) { trigRight = ScaleToTriggerLevel(rawRightGrip); trigRightActive = true; }
                    else { gripRight = rawRightGrip; gripRightActive = true; }
                    break;
            }

            // Audio Auto Haptics - derives rumble from whatever the PC is currently playing
            // (see AudioAutoHapticsCapture). Runs after RumbleMode routing so it only ever
            // touches a motor group RumbleMode has already made active, and before Adaptive
            // Trigger Simulation below so ATS - when on - still wins outright for the trigger
            // group regardless of what this does. Trigger motors pick their Audio mode and
            // noise floor from the Pulled or Idle setting depending on that side's live trigger
            // position - grip has no such state, so it just uses one mode/floor pair.
            if (_settings.AudioAutoHapticsEnabled)
            {
                byte audioLeftRaw = AudioAutoHaptics.LeftIntensity;
                byte audioRightRaw = AudioAutoHaptics.RightIntensity;

                if (gripLeftActive)
                {
                    byte audioLeft = ApplyNoiseFloor(audioLeftRaw, _settings.AudioAutoHapticsGripNoiseFloor);
                    gripLeft = ApplyAudioHapticsToGrip(_settings.AudioAutoHapticsGripMode, gripLeft, audioLeft);
                }
                if (gripRightActive)
                {
                    byte audioRight = ApplyNoiseFloor(audioRightRaw, _settings.AudioAutoHapticsGripNoiseFloor);
                    gripRight = ApplyAudioHapticsToGrip(_settings.AudioAutoHapticsGripMode, gripRight, audioRight);
                }
                if (trigLeftActive)
                {
                    var mode = leftTriggerPulled ? _settings.AudioAutoHapticsTriggerPulledMode : _settings.AudioAutoHapticsTriggerIdleMode;
                    double floorPercent = leftTriggerPulled ? _settings.AudioAutoHapticsTriggerPulledNoiseFloor : _settings.AudioAutoHapticsTriggerIdleNoiseFloor;
                    byte audioLeft = ApplyNoiseFloor(audioLeftRaw, floorPercent);
                    trigLeft = ApplyAudioHapticsToTrigger(mode, trigLeft, audioLeft);
                }
                if (trigRightActive)
                {
                    var mode = rightTriggerPulled ? _settings.AudioAutoHapticsTriggerPulledMode : _settings.AudioAutoHapticsTriggerIdleMode;
                    double floorPercent = rightTriggerPulled ? _settings.AudioAutoHapticsTriggerPulledNoiseFloor : _settings.AudioAutoHapticsTriggerIdleNoiseFloor;
                    byte audioRight = ApplyNoiseFloor(audioRightRaw, floorPercent);
                    trigRight = ApplyAudioHapticsToTrigger(mode, trigRight, audioRight);
                }
            }

            // Adaptive-trigger simulation claims the trigger motors outright when enabled -
            // grip stays under RumbleMode as computed above, but trigLeft/trigRight are
            // replaced with a live force estimate for whichever physical trigger the user is
            // actually pulling. SwapTriggers means the game thinks physical-right is its
            // "left" trigger (or vice versa), so the effect selected for each physical motor
            // has to follow the same swap the game's own axis data uses (AnalogAdjustment) -
            // otherwise a swapped setup would buzz the wrong trigger for the wrong effect.
            if (_settings.AdaptiveTriggerSimulation)
            {
                var leftPhysicalEffect  = _settings.SwapTriggers ? _lastRightTriggerEffect : _lastLeftTriggerEffect;
                var rightPhysicalEffect = _settings.SwapTriggers ? _lastLeftTriggerEffect  : _lastRightTriggerEffect;
                trigLeft  = ScaleToTriggerLevel(leftPhysicalEffect.ForceAt(state.LeftTrigger));
                trigRight = ScaleToTriggerLevel(rightPhysicalEffect.ForceAt(state.RightTrigger));

                // Opt-in workaround (AppSettings.AdaptiveTriggerDisableMatchingGrip) for a
                // confirmed device-side issue where a side's grip motor can interfere with
                // that same side's Adaptive Trigger buzz - see the setting's doc comment and
                // docs/rumble.md for the diagnosis. Suppressing that side's grip whenever its
                // trigger is meant to be actively buzzing sidesteps it entirely; the opposite
                // side's grip is unaffected.
                if (_settings.AdaptiveTriggerDisableMatchingGrip)
                {
                    if (trigLeft > 0) gripLeft = 0;
                    if (trigRight > 0) gripRight = 0;
                }
            }

            // User-configurable intensity caps, applied after mode routing so they act as a
            // final ceiling regardless of which mode picked a motor to be active. Adaptive
            // Trigger Simulation can opt out of the trigger cap specifically (AppSettings.
            // AdaptiveTriggerIgnoreIntensity) - a user may want a low general trigger-rumble
            // ceiling but still feel adaptive-trigger effects at full strength.
            gripLeft  = ScaleGripIntensity(gripLeft, _settings.GripLeftIntensity);
            gripRight = ScaleGripIntensity(gripRight, _settings.GripRightIntensity);
            if (!(_settings.AdaptiveTriggerSimulation && _settings.AdaptiveTriggerIgnoreIntensity))
            {
                trigLeft  = Math.Min(trigLeft, _settings.TriggerLeftIntensity);
                trigRight = Math.Min(trigRight, _settings.TriggerRightIntensity);
            }

            // Sent as ONE combined report whenever any of the four values changed - grip and
            // trigger share the same underlying HID report and can't be sent as two separate
            // writes without one silently overwriting the other's bytes (see
            // HidReader.TrySendMotors's doc comment for the full explanation).
            //
            // Change-detection alone isn't enough, though: a held, unchanging trigger level
            // (e.g. Adaptive Trigger Simulation buzzing steadily while the physical trigger
            // sits still) stops physically buzzing a short time after the last write, even
            // though the computed level never changed. The 0x16 trigger-fire flag is a pulse
            // the firmware needs periodically refreshed, not a state it holds indefinitely -
            // the same reason the device's general keepalive has to repeat every 250ms or it
            // stops reporting extra buttons at all (see HidReader's keepalive loop).
            // MotorRefreshInterval forces a resend on that cadence whenever any motor is meant
            // to be active, even with nothing to report as "changed".
            bool valuesChanged = gripLeft != _lastSentGripLeft || gripRight != _lastSentGripRight ||
                trigLeft != _lastSentTrigLeft || trigRight != _lastSentTrigRight;
            bool anyMotorActive = gripLeft > 0 || gripRight > 0 || trigLeft > 0 || trigRight > 0;
            bool refreshDue = anyMotorActive && DateTime.UtcNow - _lastMotorSendTime >= MotorRefreshInterval;

            if (valuesChanged || refreshDue)
            {
                HidReader.TrySendMotors(gripLeft, gripRight, trigLeft, trigRight);
                _lastSentGripLeft = gripLeft;
                _lastSentGripRight = gripRight;
                _lastSentTrigLeft = trigLeft;
                _lastSentTrigRight = trigRight;
                _lastMotorSendTime = DateTime.UtcNow;
            }
        }
    }

    // Any nonzero grip magnitude maps to at least trigger level 1, scaling up to level 4 at
    // 255 - the trigger subsystem is a coarse 0-4 discrete scale (the confirmed real max; see
    // docs/rumble.md), not continuous like grip.
    private static int ScaleToTriggerLevel(byte magnitude) =>
        magnitude == 0 ? 0 : Math.Clamp((magnitude * 4 + 254) / 255, 1, 4);

    // AudioAutoHapticsCapture no longer gates its own output (see its OnDataAvailable) since one
    // shared Left/Right intensity now feeds several motor groups, each with its own floor -
    // this applies whichever floor is relevant to the motor group being computed right now.
    private static byte ApplyNoiseFloor(byte audioMagnitude, double noiseFloorPercent) =>
        audioMagnitude < noiseFloorPercent / 100.0 * 255.0 ? (byte)0 : audioMagnitude;

    private static byte ApplyAudioHapticsToGrip(AudioAutoHapticsMode mode, byte normal, byte audio) => mode switch
    {
        AudioAutoHapticsMode.Replace => audio,
        AudioAutoHapticsMode.NormalPlusAudio => (byte)Math.Min(255, normal + audio),
        _ => normal,
    };

    private static int ApplyAudioHapticsToTrigger(AudioAutoHapticsMode mode, int normalLevel, byte audioMagnitude)
    {
        int audioLevel = ScaleToTriggerLevel(audioMagnitude);
        return mode switch
        {
            AudioAutoHapticsMode.Replace => audioLevel,
            AudioAutoHapticsMode.NormalPlusAudio => Math.Min(4, normalLevel + audioLevel),
            _ => normalLevel,
        };
    }

    // Public so InputTestView's grip test buttons can scale their ramp by the exact same
    // formula the live rumble path uses, instead of testing at raw/uncapped intensity.
    public static byte ScaleGripIntensity(byte raw, int capPercent) =>
        (byte)(raw * Math.Clamp(capPercent, 0, 100) / 100);

    /// <summary>Creates the HIDMaestro virtual controller on a background thread so the UI
    /// stays responsive during the ~700ms CreateController() call. Sets IsHidMaestroStarting
    /// while in progress so the Status tab can show a yellow "working" indicator.</summary>
    private async Task StartHidMaestroAsync()
    {
        if (IsHidMaestroStarting) return;
        IsHidMaestroStarting = true;
        UsingHidMaestro = false;
        StatusChanged?.Invoke();

        bool started = await Task.Run(() =>
        {
            if (!IsRunning) return false;
            HidMaestroOutput.Stop();
            RefreshAnalogAdjustments();
            return HidMaestroOutput.TryStart();
        });

        IsHidMaestroStarting = false;
        if (IsRunning) UsingHidMaestro = started;
        StatusChanged?.Invoke();
    }

    /// <summary>Called by the Options UI right after toggling any stick/trigger invert or swap
    /// checkbox, so HIDMaestro reflects it immediately instead of waiting for the next restart -
    /// the vJoy path already reads these settings live every tick (see OnHidStateChanged), but
    /// HidMaestroOutput caches them on its own properties for the submit loop to read without
    /// touching AppSettings from a background thread, so it needs an explicit refresh.</summary>
    public void RefreshAnalogAdjustments()
    {
        HidMaestroOutput.InvertLeftStickX  = _settings.InvertLeftStickX;
        HidMaestroOutput.InvertLeftStickY  = _settings.InvertLeftStickY;
        HidMaestroOutput.InvertRightStickX = _settings.InvertRightStickX;
        HidMaestroOutput.InvertRightStickY = _settings.InvertRightStickY;
        HidMaestroOutput.SwapSticks        = _settings.SwapSticks;
        HidMaestroOutput.SwapTriggers      = _settings.SwapTriggers;
    }

    /// <summary>Called by the HIDMaestro tab's "Save and Apply Mappings" / "Reset to Defaults"
    /// buttons. Swaps in a brand-new dictionary reference rather than mutating the existing one
    /// in place - HidMaestroOutput's submit thread reads this field on every tick with no lock,
    /// so a full reference swap (atomic in .NET) is the safe way to hand it a new map.</summary>
    public void ApplyHmButtonMap(Dictionary<HmSourceButton, HmOutputButton> map)
    {
        HidMaestroOutput.ButtonMap = map;
    }

    private void OnHidConnectionChanged(bool isConnected)
    {
        if (IsRunning && _settings.Backend == VirtualBackend.HidMaestro)
        {
            // Deferred initial start (HmConnectMode.OnControllerConnect) or an automatic
            // reconnect after HmDisconnectOnControllerDisconnect tore the controller down
            // below - either way, only fires while nothing is already active/starting.
            if (isConnected && !UsingHidMaestro && !IsHidMaestroStarting
                && (_settings.HmConnectMode == HmConnectMode.OnControllerConnect
                    || _settings.HmDisconnectOnControllerDisconnect))
            {
                _ = StartHidMaestroAsync();
            }
            // Mirrors the physical controller's connection state when opted in - independent
            // of HmConnectMode, since tearing down without an automatic way back would strand
            // the user with no controller until they revisit the Status tab themselves.
            else if (!isConnected && _settings.HmDisconnectOnControllerDisconnect && UsingHidMaestro)
            {
                StopHidMaestroForDriverMaintenance();
            }
        }

        StatusChanged?.Invoke();
    }

    /// <summary>Re-attempts HIDMaestro controller creation without a full engine stop/start -
    /// called after the driver install completes or the user clicks Retry.</summary>
    public void RetryHidMaestro()
    {
        if (!IsRunning || _settings.Backend != VirtualBackend.HidMaestro || IsHidMaestroStarting) return;
        _ = StartHidMaestroAsync();
    }

    /// <summary>
    /// Stops only the active HIDMaestro virtual controller (not HidReader/vJoy/the rest of the
    /// engine) - used both so a driver install/reinstall/uninstall can safely mutate the
    /// DriverStore package without racing HidMaestroOutput's dedicated real-time submit thread
    /// (HMContext.InstallDriver() and a manual driver-store removal both evict every bound
    /// devnode first - if our own controller is still live and submitting when that happens,
    /// the submit thread crashes the process instead of finding a clean stopped state), and by
    /// OnHidConnectionChanged when HmDisconnectOnControllerDisconnect tears the controller down
    /// on physical disconnect. Safe to call whether or not HIDMaestro is currently running.
    /// </summary>
    public void StopHidMaestroForDriverMaintenance()
    {
        HidMaestroOutput.Stop();
        UsingHidMaestro = false;
        StatusChanged?.Invoke();
    }

    public void ApplyProfile(MappingProfile profile)
    {
        Profile = profile;
        ButtonMapper.Profile = profile;
    }

    private void OnHidStateChanged(GamepadState state)
    {
        // HIDMaestro path: submit the full raw state directly (no button-mapper remapping -
        // DualSense Edge has fixed semantic positions for all buttons including the 9 extras).
        if (UsingHidMaestro)
        {
            HidMaestroOutput.SubmitState(state);
            return;
        }

        // vJoy fallback path: route through ButtonMapper for user-configurable remapping.
        ButtonMapper.Apply(SourceButton.A, state.A, VJoyOutput);
        ButtonMapper.Apply(SourceButton.B, state.B, VJoyOutput);
        ButtonMapper.Apply(SourceButton.X, state.X, VJoyOutput);
        ButtonMapper.Apply(SourceButton.Y, state.Y, VJoyOutput);
        ButtonMapper.Apply(SourceButton.LeftShoulder, state.LeftShoulder, VJoyOutput);
        ButtonMapper.Apply(SourceButton.RightShoulder, state.RightShoulder, VJoyOutput);
        ButtonMapper.Apply(SourceButton.LeftThumbClick, state.LeftThumbClick, VJoyOutput);
        ButtonMapper.Apply(SourceButton.RightThumbClick, state.RightThumbClick, VJoyOutput);
        ButtonMapper.Apply(SourceButton.Start, state.Start, VJoyOutput);
        ButtonMapper.Apply(SourceButton.Back, state.Back, VJoyOutput);
        ButtonMapper.Apply(SourceButton.Ai, state.Ai, VJoyOutput);
        ButtonMapper.Apply(SourceButton.Fn, state.Fn, VJoyOutput);
        ButtonMapper.Apply(SourceButton.Profile, state.Profile, VJoyOutput);
        ButtonMapper.Apply(SourceButton.M1, state.M1, VJoyOutput);
        ButtonMapper.Apply(SourceButton.M2, state.M2, VJoyOutput);
        ButtonMapper.Apply(SourceButton.M3, state.M3, VJoyOutput);
        ButtonMapper.Apply(SourceButton.M4, state.M4, VJoyOutput);
        ButtonMapper.Apply(SourceButton.Lm, state.Lm, VJoyOutput);
        ButtonMapper.Apply(SourceButton.Rm, state.Rm, VJoyOutput);

        VJoyOutput.SetDpad(state.DPadUp, state.DPadDown, state.DPadLeft, state.DPadRight);

        var (leftX, leftY, rightX, rightY) = AnalogAdjustment.ComputeSticks(
            state,
            _settings.InvertLeftStickX, _settings.InvertLeftStickY,
            _settings.InvertRightStickX, _settings.InvertRightStickY,
            _settings.SwapSticks);
        var (leftTrigger, rightTrigger) = AnalogAdjustment.ComputeTriggers(state, _settings.SwapTriggers);

        VJoyOutput.SetAxis(VJoyNative.HidUsage.X, VJoyOutput.FromHidByte(leftX));
        VJoyOutput.SetAxis(VJoyNative.HidUsage.Y, VJoyOutput.FromHidByte(leftY));
        VJoyOutput.SetAxis(VJoyNative.HidUsage.Z, VJoyOutput.FromHidByte(leftTrigger));
        VJoyOutput.SetAxis(VJoyNative.HidUsage.Rx, VJoyOutput.FromHidByte(rightX));
        VJoyOutput.SetAxis(VJoyNative.HidUsage.Ry, VJoyOutput.FromHidByte(rightY));
        VJoyOutput.SetAxis(VJoyNative.HidUsage.Rz, VJoyOutput.FromHidByte(rightTrigger));
    }

    public void Dispose()
    {
        HidReader.Dispose();
        HidMaestroOutput.Dispose();
        VJoyOutput.Dispose();
        AudioAutoHaptics.Dispose();
    }
}
