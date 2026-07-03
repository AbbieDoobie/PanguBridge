namespace PanguBridge.Controllers;

/// <summary>
/// Wraps the vJoy device (19 buttons, 6 axes, 1 continuous POV - see docs/button-map.md)
/// used as the virtual DirectInput joystick. Per docs/architecture.md error-handling
/// philosophy, vJoy missing/unconfigured is fatal to the app's core function, so callers
/// should surface <see cref="LastError"/> rather than silently continuing.
/// </summary>
public sealed class VJoyOutput : IDisposable
{
    private const int AxisMin = 0;
    private const int AxisMax = 32768;
    private const int PovNeutral = -1;

    private uint _deviceId;

    public VJoyOutput(int deviceId = 1) => _deviceId = (uint)deviceId;

    public bool IsAcquired { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Switches which vJoy device PanguBridge drives (1-16; see AppSettings.VJoyDeviceId).
    /// Releases the currently-acquired device first - callers should TryAcquire() again
    /// afterward.
    /// </summary>
    public void SetDeviceId(int deviceId)
    {
        Release();
        _deviceId = (uint)deviceId;
    }

    public bool TryAcquire()
    {
        if (IsAcquired) return true;

        try
        {
            if (!VJoyNative.vJoyEnabled())
            {
                LastError = "vJoy driver is not installed or not running.";
                return false;
            }

            var status = VJoyNative.GetVJDStatus(_deviceId);
            switch (status)
            {
                case VJoyNative.VjdStat.Busy:
                    LastError = $"vJoy device {_deviceId} is owned by another application.";
                    return false;
                case VJoyNative.VjdStat.Miss:
                    LastError = $"vJoy device {_deviceId} is missing - configure it in vJoyConf (19 buttons, 6 axes, 1 continuous POV).";
                    return false;
                case VJoyNative.VjdStat.Unknown:
                    LastError = $"vJoy device {_deviceId} is in an unknown state.";
                    return false;
            }

            if (!VJoyNative.AcquireVJD(_deviceId))
            {
                LastError = $"Failed to acquire vJoy device {_deviceId}.";
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            LastError = "vJoyInterface.dll was not found - install vJoy before running Pangu Bridge.";
            return false;
        }
        catch (BadImageFormatException)
        {
            LastError = "vJoyInterface.dll bitness mismatch - ensure the x64 vJoy driver is installed.";
            return false;
        }

        IsAcquired = true;
        LastError = null;
        return true;
    }

    public void Release()
    {
        if (!IsAcquired) return;
        VJoyNative.RelinquishVJD(_deviceId);
        IsAcquired = false;
    }

    public void SetButton(int vjoyButtonNumber, bool pressed)
    {
        if (!IsAcquired) return;
        VJoyNative.SetBtn(pressed, _deviceId, (byte)vjoyButtonNumber);
    }

    public void SetAxis(VJoyNative.HidUsage axis, int value)
    {
        if (!IsAcquired) return;
        VJoyNative.SetAxis(Math.Clamp(value, AxisMin, AxisMax), _deviceId, (int)axis);
    }

    /// <summary>
    /// Sets the continuous POV hat from D-pad direction bits. Adjacent bits held together
    /// (e.g. Up+Right) report the diagonal angle; no bits held reports neutral.
    /// </summary>
    public void SetDpad(bool up, bool down, bool left, bool right)
    {
        if (!IsAcquired) return;
        // vJoy's POV switches are 1-indexed, same convention as buttons (1-20) and the
        // device id itself (1-16).
        VJoyNative.SetContPov(ComputePovHundredthsOfDegree(up, down, left, right), _deviceId, 1);
    }

    public static int ComputePovHundredthsOfDegree(bool up, bool down, bool left, bool right)
    {
        if (up && right) return 4500;
        if (down && right) return 13500;
        if (down && left) return 22500;
        if (up && left) return 31500;
        if (up) return 0;
        if (right) return 9000;
        if (down) return 18000;
        if (left) return 27000;
        return PovNeutral;
    }

    /// <summary>Maps a raw HID byte (0..255) linearly into vJoy's 0..32768 axis range. Used
    /// directly for triggers and Y-axis sticks; X-axis sticks also need <see cref="InvertAxis"/>
    /// since the hardware's raw X byte runs high-to-low instead of low-to-high (see
    /// docs/hid-report-format.md).</summary>
    public static int FromHidByte(byte raw) => (int)(raw * (AxisMax / 255.0));

    /// <summary>Flips an already-mapped 0..32768 axis value to the opposite end of its range.</summary>
    public static int InvertAxis(int mappedValue) => AxisMax - mappedValue;

    public void Dispose() => Release();
}
