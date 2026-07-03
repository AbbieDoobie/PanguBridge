using System.IO;

namespace PanguBridge;

/// <summary>
/// Reads Major.Minor.Patch from version.txt (repo root, copied next to the exe on build - see
/// PanguBridge.csproj). Major/Minor are bumped by hand on request; Patch is bumped by 1 for
/// every build. Never throws - a missing or unreadable file just shows a placeholder instead
/// of crashing the app over a cosmetic label.
/// </summary>
public static class AppVersion
{
    public static string Display => TryReadVersion() is string v ? $"v{v}" : "v?.?.?";

    private static string? TryReadVersion()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
