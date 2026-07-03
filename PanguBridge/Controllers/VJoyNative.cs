using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PanguBridge.Controllers;

/// <summary>
/// Direct P/Invoke against the native vJoyInterface.dll (not the C++/CLI wrapper assembly),
/// so this project builds and runs even on machines without vJoy installed yet - it only
/// fails at the point of actually acquiring a device. See docs/architecture.md.
///
/// vJoy's installer does not add itself to PATH or register the DLL in System32, so the
/// default Windows DLL search order can't find it from our process directory. The static
/// constructor below locates the install directory and adds it to the search path before
/// any P/Invoke call below gets bound.
/// </summary>
public static class VJoyNative
{
    private const string VJoyInterface = "vJoyInterface.dll";

    /// <summary>The directory vJoyInterface.dll was found in, or null if auto-detection
    /// (and any manual override) hasn't located it.</summary>
    public static string? ResolvedDirectory { get; private set; }

    static VJoyNative()
    {
        string? dir = FindVJoyInterfaceDirectory();
        if (dir is not null) UseDirectory(dir);
    }

    /// <summary>
    /// Manual override for when auto-detection can't find vJoy (e.g. a non-standard install
    /// location). Accepts either the folder containing vJoyInterface.dll directly, or a vJoy
    /// install root that has it under an "x64" subfolder. Safe to call again later - each
    /// vJoy P/Invoke call re-attempts the native load if it hasn't already succeeded.
    /// </summary>
    /// <returns>True if vJoyInterface.dll was found at or under the given path.</returns>
    public static bool TrySetExplicitDirectory(string path)
    {
        if (File.Exists(Path.Combine(path, "vJoyInterface.dll")))
        {
            UseDirectory(path);
            return true;
        }

        string x64Dir = Path.Combine(path, "x64");
        if (File.Exists(Path.Combine(x64Dir, "vJoyInterface.dll")))
        {
            UseDirectory(x64Dir);
            return true;
        }

        return false;
    }

    private static void UseDirectory(string directory)
    {
        SetDllDirectory(directory);
        ResolvedDirectory = directory;
    }

    private static string? FindVJoyInterfaceDirectory()
    {
        foreach (var baseDir in CandidateInstallDirs())
        {
            string x64Dir = Path.Combine(baseDir, "x64");
            if (File.Exists(Path.Combine(x64Dir, "vJoyInterface.dll"))) return x64Dir;
            if (File.Exists(Path.Combine(baseDir, "vJoyInterface.dll"))) return baseDir;
        }

        return null;
    }

    private static IEnumerable<string> CandidateInstallDirs()
    {
        // Most reliable: the install location the vJoy installer registered with Windows.
        string? fromRegistry = FindInstallLocationFromUninstallRegistry();
        if (fromRegistry is not null) yield return fromRegistry;

        // Fallback: the conventional install path used by both the original vJoy and the
        // BrunnerInnovation fork.
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "vJoy");
    }

    private static string? FindInstallLocationFromUninstallRegistry()
    {
        string[] uninstallKeyPaths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var keyPath in uninstallKeyPaths)
        {
            using var uninstallKey = Registry.LocalMachine.OpenSubKey(keyPath);
            if (uninstallKey is null) continue;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var subKey = uninstallKey.OpenSubKey(subKeyName);
                if (subKey?.GetValue("DisplayName") is not string displayName) continue;
                if (!displayName.Contains("vJoy", StringComparison.OrdinalIgnoreCase)) continue;
                if (subKey.GetValue("InstallLocation") is string location && location.Length > 0)
                    return location;
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    public enum VjdStat
    {
        Own = 0,
        Free = 1,
        Busy = 2,
        Miss = 3,
        Unknown = 4,
    }

    public enum HidUsage
    {
        X = 0x30,
        Y = 0x31,
        Z = 0x32,
        Rx = 0x33,
        Ry = 0x34,
        Rz = 0x35,
    }

    [DllImport(VJoyInterface)]
    public static extern bool vJoyEnabled();

    [DllImport(VJoyInterface)]
    public static extern VjdStat GetVJDStatus(uint rID);

    [DllImport(VJoyInterface)]
    public static extern bool AcquireVJD(uint rID);

    [DllImport(VJoyInterface)]
    public static extern void RelinquishVJD(uint rID);

    [DllImport(VJoyInterface)]
    public static extern bool SetBtn(bool value, uint rID, byte nBtn);

    [DllImport(VJoyInterface)]
    public static extern bool SetAxis(int value, uint rID, int axis);

    [DllImport(VJoyInterface)]
    public static extern bool SetContPov(int value, uint rID, byte nPov);

    [DllImport(VJoyInterface)]
    public static extern int GetVJDButtonNumber(uint rID);
}
