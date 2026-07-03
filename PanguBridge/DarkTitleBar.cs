using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PanguBridge;

/// <summary>
/// Asks the OS to draw a window's native title bar (caption, min/max/close buttons) in dark
/// mode, via the same DWM attribute Windows itself uses - no custom-drawn chrome needed.
/// </summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    // 20 on Windows 10 20H1+/Windows 11; older Windows 10 builds used 19 for the same attribute.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    public static void Apply(Window window, bool dark)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
    }
}
