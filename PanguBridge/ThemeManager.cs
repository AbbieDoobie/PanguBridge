using System.Windows;

namespace PanguBridge;

/// <summary>
/// Swaps the "Colors.{Dark,Light}.xaml" resource dictionary merged into the app, while
/// "Themes/ControlStyles.xaml" (which only ever references those colors via DynamicResource)
/// stays merged at all times. DynamicResource re-resolves automatically when the underlying
/// dictionary changes, so this takes effect immediately - no app restart needed.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri ControlStylesUri = new("Themes/ControlStyles.xaml", UriKind.Relative);

    public static bool IsDark { get; private set; } = true;

    public static void Apply(string theme)
    {
        IsDark = theme != "Light";

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Clear();

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Themes/Colors.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        });
        dictionaries.Add(new ResourceDictionary { Source = ControlStylesUri });

        // Re-paint the native title bar of any window already on screen too, so toggling
        // the theme at runtime doesn't leave a light caption bar above a dark client area.
        foreach (Window window in Application.Current.Windows)
            DarkTitleBar.Apply(window, IsDark);
    }
}
