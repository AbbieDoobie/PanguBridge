using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PanguBridge.Controllers;

namespace PanguBridge.Views;

public partial class StatusView : UserControl
{
    private static readonly Brush ConnectedBrush = Brushes.SeaGreen;
    private static readonly Brush WorkingBrush   = Brushes.Goldenrod;
    private static readonly Brush ErrorBrush     = Brushes.Firebrick;
    private static readonly Brush StoppedBrush   = Brushes.Gray;

    private readonly PanguEngine _engine;
    private readonly AppSettings _settings;

    // Subscription we hold onto so we can unhook when switching away from HIDMaestro.
    private HidMaestroOutput? _hookedHm;

    public StatusView(PanguEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        VersionText.Text = AppVersion.Display;
        _engine.StatusChanged += () => Dispatcher.Invoke(Refresh);
        Refresh();
    }

    private void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        _engine.Start();
        Refresh();
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        Refresh();
    }

    private void LocateVJoyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate vJoyInterface.dll",
            FileName = "vJoyInterface.dll",
            Filter = "vJoyInterface.dll|vJoyInterface.dll|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        if (dialog.ShowDialog() != true) return;

        string? directory = Path.GetDirectoryName(dialog.FileName);
        if (directory is null || !VJoyNative.TrySetExplicitDirectory(directory))
        {
            MessageBox.Show(
                "That doesn't look like a vJoy install - vJoyInterface.dll wasn't found there.",
                "Locate vJoy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.VJoyInstallDir = VJoyNative.ResolvedDirectory;
        _settings.Save();

        _engine.VJoyOutput.TryAcquire();
        Refresh();
    }

    private void Refresh()
    {
        SetEngineIndicator();
        SetHidIndicator();
        SetBackendIndicator();
        SetFfbIndicator();
    }

    private void SetEngineIndicator()
    {
        bool running = _engine.IsRunning;
        EngineDot.Fill = running ? ConnectedBrush : StoppedBrush;
        EngineStatusText.Foreground = running ? ConnectedBrush : StoppedBrush;
        EngineStatusText.Text = running ? "Running" : "Stopped";
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
    }

    private void SetHidIndicator()
    {
        if (!_engine.IsRunning)
        {
            HidDot.Fill = StoppedBrush;
            HidStatusText.Foreground = StoppedBrush;
            HidStatusText.Text = "Stopped";
            return;
        }

        if (_engine.HidReader.IsConnected)
        {
            HidDot.Fill = ConnectedBrush;
            HidStatusText.Foreground = ConnectedBrush;
            HidStatusText.Text = "Connected";
        }
        else
        {
            HidDot.Fill = ErrorBrush;
            HidStatusText.Foreground = ErrorBrush;
            string reason = _engine.HidReader.LastError ??
                             "Plug in the Pangu dongle and close Beitong's iControl app.";
            HidStatusText.Text = $"Not detected: {reason} Retrying every 2s.";
        }
    }

    private void SetBackendIndicator()
    {
        bool usingHm = _settings.Backend == VirtualBackend.HidMaestro;

        BackendTitleText.Text = usingHm
            ? "DualSense Edge (HIDMaestro)"
            : "vJoy (external driver)";

        InstallDriverButton.Visibility = Visibility.Collapsed;
        LocateVJoyButton.Visibility    = Visibility.Collapsed;

        if (!_engine.IsRunning)
        {
            BackendDot.Fill = StoppedBrush;
            BackendStatusText.Foreground = StoppedBrush;
            BackendStatusText.Text = "Stopped";
            return;
        }

        if (usingHm)
        {
            if (_engine.HidMaestroOutput.IsRunning)
            {
                BackendDot.Fill = ConnectedBrush;
                BackendStatusText.Foreground = ConnectedBrush;
                BackendStatusText.Text = HidMaestroOutput.LibraryVersion is { } version
                    ? $"Running - version {version}"
                    : "Running";
            }
            else if (_engine.IsHidMaestroStarting)
            {
                BackendDot.Fill = WorkingBrush;
                BackendStatusText.Foreground = WorkingBrush;
                BackendStatusText.Text = "Starting...";
            }
            else if (_settings.HmConnectMode == HmConnectMode.OnControllerConnect
                     && !_engine.HidReader.IsConnected)
            {
                BackendDot.Fill = WorkingBrush;
                BackendStatusText.Foreground = WorkingBrush;
                BackendStatusText.Text = "Waiting for controller to connect...";
            }
            else if (!HidMaestroOutput.CheckIsDriverInstalled())
            {
                BackendDot.Fill = ErrorBrush;
                BackendStatusText.Foreground = ErrorBrush;
                BackendStatusText.Text = "Driver not installed - click Install Driver.";
                InstallDriverButton.Visibility = Visibility.Visible;
            }
            else
            {
                BackendDot.Fill = ErrorBrush;
                BackendStatusText.Foreground = ErrorBrush;
                BackendStatusText.Text = $"Failed: {_engine.HidMaestroOutput.LastError ?? "Unknown error."} See HIDMaestro tab.";
            }
        }
        else
        {
            if (_engine.VJoyOutput.IsAcquired)
            {
                BackendDot.Fill = ConnectedBrush;
                BackendStatusText.Foreground = ConnectedBrush;
                BackendStatusText.Text = $"Running (device {_settings.VJoyDeviceId})";
            }
            else
            {
                BackendDot.Fill = ErrorBrush;
                BackendStatusText.Foreground = ErrorBrush;
                string reason = _engine.VJoyOutput.LastError ?? "vJoy not available.";
                BackendStatusText.Text = $"Not running: {reason} Retrying every 2s.";
                LocateVJoyButton.Visibility = Visibility.Visible;
            }
        }
    }

    private async void InstallDriverButton_OnClick(object sender, RoutedEventArgs e)
    {
        InstallDriverButton.IsEnabled = false;
        BackendStatusText.Foreground = StoppedBrush;
        BackendStatusText.Text = "Installing...";

        // InstallDriver() evicts every bound devnode itself - stop our own controller first so
        // its submit thread isn't still writing to the device out from under that eviction.
        _engine.StopHidMaestroForDriverMaintenance();

        var (success, error) = await Task.Run(App.InstallHidMaestroDriver);

        if (!success)
        {
            BackendStatusText.Foreground = ErrorBrush;
            BackendStatusText.Text = $"Install failed: {error}";
            InstallDriverButton.IsEnabled = true;
            return;
        }

        // Auto-retry controller creation now that the driver should be present.
        _engine.RetryHidMaestro();

        InstallDriverButton.IsEnabled = true;
        Refresh();
    }

    private void SetFfbIndicator()
    {
        bool usingHm = _settings.Backend == VirtualBackend.HidMaestro;

        FfbTitleText.Text = "Rumble";

        if (usingHm)
        {
            HookHidMaestro(_engine.HidMaestroOutput);

            if (!_engine.IsRunning)
            {
                FfbDot.Fill = StoppedBrush;
                FfbStatusText.Foreground = StoppedBrush;
                FfbStatusText.Text = "Stopped";
            }
            else if (!_engine.HidMaestroOutput.IsRunning)
            {
                FfbDot.Fill = StoppedBrush;
                FfbStatusText.Foreground = StoppedBrush;
                FfbStatusText.Text = "Waiting for HIDMaestro...";
            }
            else
            {
                FfbDot.Fill = ConnectedBrush;
                FfbStatusText.Foreground = ConnectedBrush;
                FfbStatusText.Text = "Active";
            }
        }
        else
        {
            UnhookHidMaestro();

            // vJoy backend has no rumble support at all: this is a static informational
            // line, not a live status (there's no watchdog/registration to track anymore).
            FfbDot.Fill = StoppedBrush;
            FfbStatusText.Foreground = StoppedBrush;
            FfbStatusText.Text = "Not supported on vJoy";
        }
    }

    private void HookHidMaestro(HidMaestroOutput hm)
    {
        if (ReferenceEquals(hm, _hookedHm)) return;
        UnhookHidMaestro();
        _hookedHm = hm;
    }

    private void UnhookHidMaestro()
    {
        _hookedHm = null;
    }
}
