using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PanguBridge.Controllers;

namespace PanguBridge.Views;

public partial class VJoyLegacyView : UserControl
{
    private static readonly Brush SuccessBrush = Brushes.SeaGreen;
    private static readonly Brush ErrorBrush   = Brushes.Firebrick;

    private readonly PanguEngine _engine;
    private readonly AppSettings _settings;
    private bool _suppressEvents;

    public VJoyLegacyView(PanguEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;

        _suppressEvents = true;
        VJoyDeviceIdTextBox.Text = _settings.VJoyDeviceId.ToString();
        _suppressEvents = false;

        MappingHost.Content = new MappingEditor(_engine, _settings);
        SteamIntegrationHost.Content = new SteamIntegrationView(_engine);
    }

    private void VJoyDeviceIdTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!int.TryParse(VJoyDeviceIdTextBox.Text, out int deviceId) || deviceId is < 1 or > 16)
        {
            VJoyDeviceIdTextBox.Text = _settings.VJoyDeviceId.ToString();
            return;
        }
        _settings.VJoyDeviceId = deviceId;
        _settings.Save();
        _engine.VJoyOutput.SetDeviceId(deviceId);
        _engine.VJoyOutput.TryAcquire();
    }

    private async void ConfigureVJoyButton_OnClick(object sender, RoutedEventArgs e)
    {
        int deviceId = _settings.VJoyDeviceId;
        ConfigureVJoyButton.IsEnabled = false;
        ConfigureVJoyResultText.Visibility = Visibility.Visible;
        ConfigureVJoyResultText.Foreground = SystemColors.GrayTextBrush;
        ConfigureVJoyResultText.Text = "Configuring...";

        (bool ok, string? error) = await Task.Run(() =>
        {
            bool ok = VJoyConfigCli.TryConfigureDevice(deviceId, out string? error);
            return (ok, error);
        });

        ConfigureVJoyButton.IsEnabled = true;
        if (ok)
        {
            ConfigureVJoyResultText.Foreground = SuccessBrush;
            ConfigureVJoyResultText.Text = $"vJoy device {deviceId} configured. Retrying acquisition...";
            _engine.VJoyOutput.TryAcquire();
        }
        else
        {
            ConfigureVJoyResultText.Foreground = ErrorBrush;
            ConfigureVJoyResultText.Text = $"Configuration failed: {error}";
        }
    }
}
