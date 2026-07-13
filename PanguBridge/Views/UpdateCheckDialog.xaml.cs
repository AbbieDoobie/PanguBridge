using System.Diagnostics;
using System.Windows;

namespace PanguBridge.Views;

/// <summary>
/// Result popup for the Options tab's "Check for Update" button (see
/// SettingsView.CheckForUpdateButton_OnClick). Plain MessageBox can't give its buttons custom
/// text ("View Releases on GitHub" instead of "Yes"/"No"), so this is a small dedicated window
/// instead. actionUrl null/omitted means "up to date" - only Close is shown. latestVersion null
/// means the check failed - the "Latest release" line is hidden rather than shown blank, since
/// there's nothing to report.
/// </summary>
public partial class UpdateCheckDialog : Window
{
    private readonly string? _actionUrl;

    public UpdateCheckDialog(string message, string localVersion, string? latestVersion,
        string? actionButtonText = null, string? actionUrl = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this, ThemeManager.IsDark);

        MessageText.Text = message;
        LocalVersionRun.Text = localVersion;
        _actionUrl = actionUrl;

        if (latestVersion is null)
        {
            LatestVersionText.Visibility = Visibility.Collapsed;
        }
        else
        {
            LatestVersionRun.Text = latestVersion;
        }

        if (actionButtonText is null || actionUrl is null)
        {
            ActionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ActionButton.Content = actionButtonText;
        }
    }

    private void ActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_actionUrl is not null)
            Process.Start(new ProcessStartInfo(_actionUrl) { UseShellExecute = true });

        Close();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
