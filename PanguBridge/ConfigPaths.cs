using System.IO;

namespace PanguBridge;

public static class ConfigPaths
{
    private static readonly string ExeDir =
        AppDomain.CurrentDomain.BaseDirectory;

    public static bool IsPortable => File.Exists(Path.Combine(ExeDir, "portable.txt"));

    public static string BaseDir => IsPortable
        ? Path.Combine(ExeDir, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PanguBridge");

    /// <summary>Everything PanguBridge persists lives in this one file, including the vJoy
    /// button mapping (AppSettings.VJoyButtonMap) - there is no separate profiles folder.</summary>
    public static string SettingsFile => Path.Combine(BaseDir, "settings.json");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(BaseDir);
    }
}
