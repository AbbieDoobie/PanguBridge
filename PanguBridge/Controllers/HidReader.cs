using System.IO;
using HidSharp;

namespace PanguBridge.Controllers;

/// <summary>
/// Reads the full gamepad state - every standard button/axis/trigger/d-pad control plus
/// the 9 vendor-defined extra buttons - from HID Interface 4 (vendor usage page 0xFF80) of
/// the Beitong Pangu dongle. See docs/usb-reverse-engineering.md and docs/hid-report-format.md.
/// </summary>
public sealed class HidReader : IDisposable
{
    public const int VendorId = 0x20BC;
    public const int ProductId = 0x5162;

    private const int ReconnectDelayMs = 2000;
    private const int KeepaliveIntervalMs = 250;
    private const int ReadTimeoutMs = 1000;
    private const int TriggerGainQueryTimeoutMs = 1500;

    public event Action<GamepadState>? StateChanged;
    public event Action<bool>? ConnectionChanged;

    /// <summary>Fired once per connect with the controller's actual persisted Trigger Gain Mode
    /// (read from flash, not assumed) - see QueryTriggerGainMode - and again whenever
    /// TrySetTriggerGainMode changes it.</summary>
    public event Action<bool>? TriggerGainModeChanged;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private GamepadState _lastState = GamepadState.Idle;

    // Set while a session is active so TrySendMotors can write to the same stream the
    // read/keepalive loops are using. _writeLock serializes all three writers - HidStream
    // doesn't document concurrent-write safety, and the keepalive loop already writes every
    // 250ms independent of vibration commands.
    private readonly object _writeLock = new();
    private HidStream? _activeStream;
    private int _activeOutputLength;

    // Last trigger levels actually configured via the 0x62 packet, so TrySendMotors only
    // resends it when the levels genuinely change (not on every combined-report send).
    private int _lastConfiguredTriggerLeft = -1;
    private int _lastConfiguredTriggerRight = -1;

    public GamepadState CurrentState => _lastState;
    public bool IsConnected { get; private set; }

    /// <summary>The controller's Trigger Gain Mode, as read from its own flash on connect (see
    /// QueryTriggerGainMode) - not an app-side setting, since the controller itself remembers
    /// this across power cycles independent of PanguBridge. Defaults to false until the first
    /// successful connect/query.</summary>
    public bool TriggerGainMode { get; private set; }

    /// <summary>Diagnostic detail for the current disconnected state - what FindDevice saw,
    /// or the exact exception from opening/enabling/reading the device. Null while connected.</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_runTask is not null) return;
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _runTask?.Wait(ReadTimeoutMs * 2);
        }
        catch (Exception)
        {
            // Best-effort shutdown; swallow timeout/aggregate exceptions from cancellation.
        }
        _runTask = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var (device, findDiagnostic) = FindDevice();
            if (device is null)
            {
                LastError = findDiagnostic;
                SetConnected(false);
                if (!await DelaySafeAsync(ReconnectDelayMs, token)) return;
                continue;
            }

            try
            {
                await RunSessionAsync(device, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                SetConnected(false);
            }

            if (!await DelaySafeAsync(ReconnectDelayMs, token)) return;
        }
    }

    private async Task RunSessionAsync(HidDevice device, CancellationToken token)
    {
        int maxInputLength = device.GetMaxInputReportLength();
        int maxOutputLength = device.GetMaxOutputReportLength();

        using var stream = device.Open();
        stream.ReadTimeout = ReadTimeoutMs;
        stream.WriteTimeout = ReadTimeoutMs;

        try
        {
            SendEnable(stream, maxOutputLength);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Sending the enable command failed ({ex.GetType().Name}: {ex.Message}). Device reports " +
                $"MaxInputReportLength={maxInputLength}, MaxOutputReportLength={maxOutputLength} - if these " +
                "differ from the 64/65 bytes this app assumes, that's likely the cause.", ex);
        }

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var keepaliveTask = Task.Run(() => KeepaliveLoopAsync(stream, maxOutputLength, sessionCts.Token), sessionCts.Token);

        // Read the controller's actual persisted Trigger Gain Mode before publishing
        // _activeStream - any config-packet write before this point (e.g. an LED color already
        // queued up) would carry _ledZoneFlags's still-default value and silently reset the
        // controller's real state. See docs/led.md.
        QueryTriggerGainMode(stream, maxOutputLength, maxInputLength);

        SetConnected(true);
        _activeStream = stream;
        _activeOutputLength = maxOutputLength;

        try
        {
            var buffer = new byte[maxInputLength];
            while (!token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    // No report within ReadTimeoutMs - loop back to re-check cancellation.
                    continue;
                }

                if (read <= 0) break;
                ProcessReport(buffer);
            }
        }
        finally
        {
            _activeStream = null;
            sessionCts.Cancel();
            try
            {
                await keepaliveTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // Fixed preamble for the 0x62 "configure" command - covers only the bytes that are truly
    // constant regardless of trigger/LED state (bytes [2]-[8]); see docs/led.md for the full
    // byte-level decode of the rest of this report, including bytes [9]-[12] which carry LED
    // color/zone state. Reproduced as-is rather than reconstructed since the meaning of these
    // 7 bytes isn't confirmed.
    private static readonly byte[] ConfigPreamble =
    {
        0x00, 0x0C, 0x3C, 0x96, 0xA5, 0xC3, 0x01, // [2]-[8]
    };

    // Trigger Gain Mode's bit within the 0x62 packet's byte[12] (docs/led.md's LED zone/flash
    // bitmask byte) - confirmed via USBPcap capture of iControl's own toggle (7 flips, 7/7
    // correlated) and independently confirmed readable back via BaseProfileQuery below.
    private const byte TriggerGainBit = 0x20;

    // getProfileData(type=1 "base config", adr=0, len=48) request, reverse-engineered from
    // iControl's own protocol layer (pgv1.js's getProfileData/getProfileConfig) - queries the
    // controller's persisted base-config blob directly from flash. The response (report id
    // 0x52) carries the exact same byte layout as the 0x62 configure write below, including
    // byte[12], which is the only confirmed way to read Trigger Gain Mode back from the
    // controller instead of just assuming it. See docs/led.md.
    private static readonly byte[] BaseProfileQuery = { 0x02, 0x52, 0x00, 0x0C };

    // Cached LED state - every 0x62 write carries a full snapshot of trigger level AND LED
    // state together (the device has no concept of a partial update, same as the 0x16 grip/
    // trigger report below), so this has to be cached and resent on every write, including
    // ones only meant to change trigger level. Defaults match the Pangu's factory-default
    // state (white, max brightness, all zones on, flash-on-vibrate off) - see docs/led.md.
    private byte _ledRed = 0xFF, _ledGreen = 0xFF, _ledBlue = 0xFF;
    // Zone-disable/flash-on-vibrate bits plus Trigger Gain Mode (docs/led.md's byte[12]) -
    // QueryTriggerGainMode overwrites this with the controller's real value on every connect
    // before anything can write it, so an unrelated zone/flash bit a user set via iControl
    // survives instead of being silently reset to this "all zones on, flash off, gain off"
    // default. TrySetTriggerGainMode only ever flips TriggerGainBit within whatever this
    // currently holds.
    private byte _ledZoneFlags = 0x00;
    private byte _ledBrightness = 4;
    private bool _ledPendingSend = true;

    /// <summary>
    /// Drives grip and trigger motors together in one combined report. Grip and trigger are
    /// two separate physical subsystems, but they share the same output report type (0x02,
    /// byte[1]=0x16) and byte range - grip's continuous magnitude lives in byte[2]/byte[3],
    /// while the trigger fire flags (0xFF=start at whatever level was last configured via
    /// 0x62, 0x00=stop) live in byte[4]/byte[5]. The device has no concept of a partial update
    /// - each 0x16 write is the complete truth for all 4 bytes at once, so grip and trigger
    /// must be sent as a single combined report rather than two independent writes, or whichever
    /// is sent last silently stomps the other's bytes.
    ///
    /// byte[4]/[5] mirrors grip's own magnitude only when that side's trigger isn't meant to
    /// be firing (level 0), reproducing plain grip-only or trigger-only behavior
    /// (RumbleMode.GripOnly / TriggerOnly / GripOrTriggerIfPulled, where only one subsystem is
    /// ever active per side at a time) when nothing needs to be combined, and only diverging
    /// from it when both a grip magnitude and that side's trigger fire are genuinely requested
    /// simultaneously (GripAndTrigger / GripAndTriggerIfPulled).
    /// </summary>
    // AppSettings.AdaptiveTriggerIncludeGainLevels - Trigger Gain Mode toggled per-tick as an
    // extra amplitude tier for self-oscillating Adaptive Trigger effects. Tracks the last
    // gainOverride actually sent so a change in just this (with trigger levels otherwise
    // unchanged) still forces a resend - see TrySendMotors.
    private bool? _lastConfiguredGainOverride;

    /// <summary>gainOverride, when non-null, temporarily flips Trigger Gain Mode's bit for this
    /// write only, without touching the persisted _ledZoneFlags/TriggerGainMode the user's own
    /// Options-tab checkbox controls - null leaves that persisted state as-is (the normal
    /// path). See AppSettings.AdaptiveTriggerIncludeGainLevels and PanguEngine's
    /// self-oscillating ATS handling.</summary>
    public bool TrySendMotors(byte gripLeft, byte gripRight, int triggerLeftLevel, int triggerRightLevel,
        bool? gainOverride = null)
    {
        var stream = _activeStream;
        if (stream is null) return false;

        // 0-4: the wire byte's high nibble (see SendConfigurePacket) technically has room for
        // 0-15, but 4 is the confirmed real max - values above it don't increase strength and
        // 5 specifically produces a wrong-feeling buzz. See docs/rumble.md.
        triggerLeftLevel = Math.Clamp(triggerLeftLevel, 0, 4);
        triggerRightLevel = Math.Clamp(triggerRightLevel, 0, 4);

        try
        {
            lock (_writeLock)
            {
                // Resends on every call where a trigger is active, not just when the level
                // value changes, since a held/unchanging level otherwise only gets sent once -
                // the firmware needs the 0x62 report refreshed periodically to keep a trigger
                // buzzing, not just on the initial level change. Both call sites (PanguEngine's
                // ~10 Hz-max periodic refresh, InputTestView's 2.5 Hz-max test ramp) already
                // throttle how often they call this method, so resending unconditionally here
                // doesn't spam the device. Also resends whenever LED state changed since the
                // last configure send, or gainOverride differs from what was last sent - see
                // SendConfigurePacket.
                bool configSent = triggerLeftLevel != _lastConfiguredTriggerLeft || triggerRightLevel != _lastConfiguredTriggerRight
                    || triggerLeftLevel > 0 || triggerRightLevel > 0 || _ledPendingSend
                    || gainOverride != _lastConfiguredGainOverride;
                if (configSent)
                {
                    SendConfigurePacket(stream, triggerLeftLevel, triggerRightLevel, gainOverride);
                    _lastConfiguredTriggerLeft = triggerLeftLevel;
                    _lastConfiguredTriggerRight = triggerRightLevel;
                    _lastConfiguredGainOverride = gainOverride;
                }

                var report = new byte[_activeOutputLength];
                report[0] = 0x02;
                report[1] = 0x16;
                report[2] = gripLeft;
                report[3] = gripRight;
                report[4] = triggerLeftLevel > 0 ? (byte)0xFF : gripLeft;
                report[5] = triggerRightLevel > 0 ? (byte)0xFF : gripRight;
                stream.Write(report);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sets the controller's LED color (0-255 per channel, standard R,G,B order -
    /// callers don't need to know the device's own B,G,R wire order, see SendConfigurePacket).
    /// Sends immediately, reusing whatever trigger level was last configured so this doesn't
    /// disturb an in-progress trigger buzz - see docs/led.md. Also re-sends
    /// _lastConfiguredGainOverride so this doesn't clobber Adaptive Trigger Simulation's
    /// temporary gain-bit flip if one is active mid-buzz (see TrySendMotors's gainOverride
    /// parameter) - full-buffer-replace output reports mean this write otherwise resets the gain
    /// bit to whatever the persisted _ledZoneFlags says until the next gate-loop tick corrects
    /// it.</summary>
    public bool TrySetLedColor(byte red, byte green, byte blue)
    {
        var stream = _activeStream;
        if (stream is null) return false;

        try
        {
            lock (_writeLock)
            {
                _ledRed = red;
                _ledGreen = green;
                _ledBlue = blue;
                SendConfigurePacket(stream, _lastConfiguredTriggerLeft < 0 ? 0 : _lastConfiguredTriggerLeft,
                    _lastConfiguredTriggerRight < 0 ? 0 : _lastConfiguredTriggerRight,
                    _lastConfiguredGainOverride);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sets Trigger Gain Mode - a controller-side setting persisted on the Pangu itself
    /// (see QueryTriggerGainMode), not an app-level one, so there's nothing to save to
    /// settings.json here. Sends immediately, reusing whatever trigger level was last configured
    /// so this doesn't disturb an in-progress trigger buzz - see docs/led.md. Also re-sends
    /// _lastConfiguredGainOverride (see TrySetLedColor's doc comment for why) so this doesn't
    /// momentarily fight with Adaptive Trigger Simulation's own temporary gain-bit flip if one is
    /// active at the same instant; the LED color itself (_ledRed/_ledGreen/_ledBlue) is always
    /// carried forward automatically by SendConfigurePacket regardless of which method calls
    /// it.</summary>
    public bool TrySetTriggerGainMode(bool enabled)
    {
        var stream = _activeStream;
        if (stream is null) return false;

        try
        {
            lock (_writeLock)
            {
                _ledZoneFlags = enabled
                    ? (byte)(_ledZoneFlags | TriggerGainBit)
                    : (byte)(_ledZoneFlags & ~TriggerGainBit);
                SendConfigurePacket(stream, _lastConfiguredTriggerLeft < 0 ? 0 : _lastConfiguredTriggerLeft,
                    _lastConfiguredTriggerRight < 0 ? 0 : _lastConfiguredTriggerRight,
                    _lastConfiguredGainOverride);
            }
            TriggerGainMode = enabled;
            TriggerGainModeChanged?.Invoke(enabled);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Builds and sends the 0x62 "configure" report - the device has no concept of a
    /// partial update, so every write carries a full snapshot of both trigger level (from the
    /// caller) and LED state (from the cached _led* fields) at once, regardless of which one
    /// actually changed. See docs/led.md for the full byte-level decode. gainOverride, when
    /// non-null, flips TriggerGainBit for this write only without touching the persisted
    /// _ledZoneFlags - see TrySendMotors.</summary>
    private void SendConfigurePacket(HidStream stream, int triggerLeftLevel, int triggerRightLevel,
        bool? gainOverride = null)
    {
        var configure = new byte[_activeOutputLength];
        configure[0] = 0x02;
        configure[1] = 0x62;
        Array.Copy(ConfigPreamble, 0, configure, 2, ConfigPreamble.Length);
        configure[9]  = _ledBlue;
        configure[10] = _ledGreen;
        configure[11] = _ledRed;
        configure[12] = gainOverride is null ? _ledZoneFlags
            : gainOverride.Value ? (byte)(_ledZoneFlags | TriggerGainBit) : (byte)(_ledZoneFlags & ~TriggerGainBit);
        configure[13] = (byte)((triggerLeftLevel << 4) | (_ledBrightness & 0x0F));
        configure[14] = (byte)(triggerRightLevel * 0x10 + 0x04);
        configure[15] = 0x04;
        configure[16] = 0x00;
        configure[17] = 0xFF;
        configure[18] = 0xFF;
        stream.Write(configure);
        _ledPendingSend = false;
    }

    /// <summary>Sends BaseProfileQuery and waits (synchronously - called from RunSessionAsync
    /// before the main read loop starts) for the controller's 0x52 reply, caching byte[12] into
    /// _ledZoneFlags so TriggerGainMode reflects reality instead of this class's own in-memory
    /// default. Best-effort: on timeout or write failure, leaves TriggerGainMode/_ledZoneFlags
    /// exactly as they were (false/0x00 on a first-ever connect) rather than blocking the
    /// session - a stale/default read is far less disruptive than never connecting.</summary>
    private void QueryTriggerGainMode(HidStream stream, int outputLength, int inputLength)
    {
        var query = new byte[outputLength];
        Array.Copy(BaseProfileQuery, query, BaseProfileQuery.Length);

        try
        {
            // The keepalive loop is already running by this point (started just above in
            // RunSessionAsync) and writes to this same stream every 250ms - _writeLock is the
            // same lock TrySendMotors/TrySetLedColor/the keepalive loop all use to serialize
            // writes, since HidStream doesn't document concurrent-write safety.
            lock (_writeLock) stream.Write(query);
        }
        catch (Exception)
        {
            return;
        }

        var buffer = new byte[inputLength];
        var deadline = DateTime.UtcNow.AddMilliseconds(TriggerGainQueryTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, inputLength);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception)
            {
                return;
            }

            if (read < 13 || buffer[0] != 0x02 || buffer[1] != 0x52) continue; // not the reply

            _ledZoneFlags = buffer[12];
            TriggerGainMode = (buffer[12] & TriggerGainBit) != 0;
            TriggerGainModeChanged?.Invoke(TriggerGainMode);
            return;
        }
    }

    private void ProcessReport(byte[] report)
    {
        if (report.Length < 18) return; // shorter than the documented 0x25 layout needs
        if (report[0] != 0x02) return; // unexpected report ID
        if (report[1] != 0x25) return; // 0x21/0x31/0x11/0x37 are heartbeat/status - ignore

        var state = GamepadState.FromReport(report);
        if (state == _lastState) return;

        _lastState = state;
        StateChanged?.Invoke(state);
    }

    private static void SendEnable(HidStream stream, int reportLength)
    {
        // Report ID (0x02) is byte[0], matching the read side and the device's own raw USB
        // capture (docs/usb-reverse-engineering.md) - not byte[1]. The buffer must be sized
        // from the device's actual MaxOutputReportLength (64, not 65) - a mismatched buffer
        // size causes WriteFile to fail with ERROR_INVALID_PARAMETER.
        var enable = new byte[reportLength];
        enable[0] = 0x02;
        enable[1] = 0x37;
        stream.Write(enable);
    }

    private async Task KeepaliveLoopAsync(HidStream stream, int reportLength, CancellationToken token)
    {
        bool toggle = true;
        while (!token.IsCancellationRequested)
        {
            var report = new byte[reportLength];
            report[0] = 0x02;
            report[1] = toggle ? (byte)0x25 : (byte)0x21;

            try
            {
                lock (_writeLock) stream.Write(report);
            }
            catch (Exception)
            {
                return; // stream closed/disconnected - let the read loop notice and reconnect
            }

            toggle = !toggle;
            if (!await DelaySafeAsync(KeepaliveIntervalMs, token)) return;
        }
    }

    private static (HidDevice? Device, string Diagnostic) FindDevice()
    {
        var candidates = DeviceList.Local.GetHidDevices(vendorID: VendorId, productID: ProductId).ToList();

        if (candidates.Count == 0)
        {
            return (null, $"No HID device found for VID 0x{VendorId:X4} / PID 0x{ProductId:X4}. " +
                          "Make sure the Pangu's wireless USB receiver is plugged in.");
        }

        var byPath = candidates.FirstOrDefault(d => d.DevicePath.Contains("MI_04", StringComparison.OrdinalIgnoreCase));
        if (byPath is not null) return (byPath, "");

        // Path matching can fail if Windows changes its device path scheme; fall back to
        // identifying Interface 4 by its vendor-defined usage page.
        foreach (var device in candidates)
        {
            if (TryGetUsagePage(device, out int usagePage) && usagePage == 0xFF80)
                return (device, "");
        }

        string seen = string.Join("; ", candidates.Select(d =>
        {
            TryGetUsagePage(d, out int page);
            return $"{d.DevicePath} (usage page 0x{page:X4})";
        }));
        return (null, $"Found {candidates.Count} HID device(s) for this VID/PID, but none matched Interface 4 " +
                      $"(path containing \"MI_04\" or usage page 0xFF80). Seen: {seen}");
    }

    private static bool TryGetUsagePage(HidDevice device, out int usagePage)
    {
        try
        {
            var descriptor = device.GetReportDescriptor();
            foreach (var item in descriptor.DeviceItems)
            {
                foreach (var usage in item.Usages.GetAllValues())
                {
                    usagePage = (int)(usage >> 16);
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Descriptor unavailable or unparsable for this device - caller treats as no match.
        }

        usagePage = 0;
        return false;
    }

    private void SetConnected(bool connected)
    {
        if (connected) LastError = null;
        if (IsConnected == connected) return;
        IsConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    private static async Task<bool> DelaySafeAsync(int milliseconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(milliseconds, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
