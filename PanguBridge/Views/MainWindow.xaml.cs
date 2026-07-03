using System.Windows;
using System.Windows.Controls;

namespace PanguBridge.Views;

public partial class MainWindow : Window
{
    public MainWindow(PanguEngine engine, AppSettings settings)
    {
        InitializeComponent();

        ((TabItem)MainTabs.Items[0]).Content = new StatusView(engine, settings);
        ((TabItem)MainTabs.Items[1]).Content = new SettingsView(engine, settings);
        ((TabItem)MainTabs.Items[2]).Content = new InputTestView(engine, settings);
        ((TabItem)MainTabs.Items[3]).Content = new HidMaestroView(engine, settings);
        ((TabItem)MainTabs.Items[4]).Content = new VJoyLegacyView(engine, settings);

        // The window's HWND doesn't exist until this fires - too early in the constructor.
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this, ThemeManager.IsDark);
    }

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window only hides it; the tray icon keeps the app running.
        e.Cancel = true;
        Hide();
    }
}
