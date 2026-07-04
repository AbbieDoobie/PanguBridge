using System.Diagnostics;

namespace PanguBridge;

/// <summary>
/// Registers PanguBridge to launch at user logon via Task Scheduler, not the HKCU Run key.
/// app.manifest requests requireAdministrator (HIDMaestro needs it for both InstallDriver()
/// and CreateController()), and a plain Run-key entry does not auto-elevate - Windows would
/// either show a UAC prompt at every login (breaking silent autostart) or the launch would
/// simply fail. A Task Scheduler task with /RL HIGHEST launches already elevated, with no
/// interactive prompt, which is the standard way to autostart an admin-manifested app.
///
/// PanguBridge itself already runs elevated by the time any of this code executes (its own
/// manifest guarantees that), so schtasks.exe here inherits that elevated token directly -
/// no additional "runas"/UAC prompt is needed for Create or Delete.
/// </summary>
public static class Autostart
{
    private const string TaskName = "PanguBridge";
    private const int ProcessTimeoutMs = 10_000;

    public static bool IsEnabled()
    {
        try
        {
            var (exitCode, stdout) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML", ProcessTimeoutMs);
            // Exit code alone only tells us the task exists - also check it still points at
            // this exact executable, so a stale task from a moved/reinstalled copy doesn't
            // read as "enabled" when it would actually launch the wrong (or missing) file.
            return exitCode == 0 && stdout.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <returns>True on success. On failure, <paramref name="error"/> holds a human-readable
    /// reason - schtasks.exe's own stderr text, or the exception message if it couldn't even
    /// be launched.</returns>
    public static bool Enable(out string? error)
    {
        error = null;
        try
        {
            // /F forces overwrite of any existing task with this name, so repeated
            // enable/disable/enable never creates duplicates - it just replaces itself.
            // /RL HIGHEST is what makes this launch pre-elevated, with no logon UAC prompt.
            // --minimized tells App.OnStartup not to show the main window - a manual double-click
            // launch (no argument) still opens visibly as normal.
            string args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F";
            var (exitCode, stdout) = RunSchtasks(args, ProcessTimeoutMs);
            if (exitCode == 0) return true;

            error = string.IsNullOrWhiteSpace(stdout) ? $"schtasks.exe exited with code {exitCode}." : stdout.Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <returns>True on success, including "the task was already gone" (nothing to disable is
    /// not a failure). On genuine failure, <paramref name="error"/> holds a human-readable
    /// reason.</returns>
    public static bool Disable(out string? error)
    {
        error = null;
        try
        {
            var (exitCode, stdout) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", ProcessTimeoutMs);
            if (exitCode == 0) return true;

            // schtasks reports this distinctly when the task doesn't exist - that's the
            // outcome we want anyway (disabled), not an error to surface to the user.
            if (stdout.Contains("cannot find", StringComparison.OrdinalIgnoreCase)) return true;

            error = string.IsNullOrWhiteSpace(stdout) ? $"schtasks.exe exited with code {exitCode}." : stdout.Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Runs schtasks.exe non-interactively and captures combined stdout+stderr text
    /// (schtasks writes its error messages to stdout, not stderr, in practice) plus the exit
    /// code. UseShellExecute=false with no "runas" verb: PanguBridge is already elevated, so
    /// this child process inherits that token without a second UAC prompt.</summary>
    private static (int ExitCode, string Output) RunSchtasks(string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch schtasks.exe.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* best-effort */ }
            throw new TimeoutException("schtasks.exe did not respond in time.");
        }

        string combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (process.ExitCode, combined);
    }

    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not determine the running executable path.");
}
