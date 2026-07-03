using System.Diagnostics;
using System.IO;

namespace PanguBridge.Controllers;

/// <summary>
/// Shells out to vJoyConfig.exe to configure a vJoy device - not exposed through
/// vJoyInterface.dll's API (confirmed via dumpbin /exports - there's no device-configuration
/// function at all). This changes driver state and normally needs admin, but PanguBridge's
/// own process already runs elevated for its whole lifetime (app.manifest), so vJoyConfig.exe
/// is launched directly rather than via the "runas" verb - that verb always triggers its own
/// separate consent prompt regardless of whether the calling process is already elevated,
/// which would just be a redundant second UAC prompt here. See docs/architecture.md.
/// </summary>
public static class VJoyConfigCli
{
    /// <summary>
    /// Configures the given vJoy device (see AppSettings.VJoyDeviceId) with exactly what
    /// PanguBridge needs: 19 buttons, the 6 axes used for sticks/triggers (X, Y, Z, Rx, Ry,
    /// Rz), and 1 continuous POV hat for the D-pad. vJoy is a buttons/sticks-only backend
    /// here (no rumble support), so Force Feedback is deliberately left disabled. "-f" forces
    /// recreation even if the device already exists with different settings: this
    /// intentionally overwrites whatever that device id currently has configured.
    /// </summary>
    public static bool TryConfigureDevice(int deviceId, out string? error) =>
        TryRun($"{deviceId} -f -a x y z rx ry rz -b 19 -p 1", out error);

    private static bool TryRun(string arguments, out string? error)
    {
        string? dir = VJoyNative.ResolvedDirectory;
        if (dir is null)
        {
            error = "vJoy install directory is unknown - use Locate vJoy manually on the Status tab first.";
            return false;
        }

        string exePath = Path.Combine(dir, "vJoyConfig.exe");
        if (!File.Exists(exePath))
        {
            error = $"vJoyConfig.exe was not found in {dir}.";
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
            });

            if (process is null)
            {
                error = "Failed to launch vJoyConfig.exe.";
                return false;
            }

            if (!process.WaitForExit(60_000))
            {
                error = "vJoyConfig.exe is still running - give it a few more seconds and check again.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                error = $"vJoyConfig.exe exited with code {process.ExitCode}.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
