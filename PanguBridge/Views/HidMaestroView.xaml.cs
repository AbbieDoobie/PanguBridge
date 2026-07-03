using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PanguBridge.Controllers;
using PanguBridge.Mapping;

namespace PanguBridge.Views;

public partial class HidMaestroView : UserControl
{
    private static readonly Brush SuccessBrush = Brushes.SeaGreen;
    private static readonly Brush ErrorBrush   = Brushes.Firebrick;
    private static readonly Brush NeutralBrush = Brushes.Gray;

    private readonly PanguEngine _engine;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<HmMappingRow> _mappingRows = new();
    private bool _suppressEvents;

    public HidMaestroView(PanguEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;

        _suppressEvents = true;
        (settings.HmConnectMode == HmConnectMode.OnControllerConnect ? HmOnConnectRadio : HmOnStartRadio).IsChecked = true;
        HmDisconnectOnControllerDisconnectCheckBox.IsChecked = settings.HmDisconnectOnControllerDisconnect;
        HmSubmitRateSlider.Value = settings.HmSubmitRateHz;
        HmSubmitRateValueText.Text = settings.HmSubmitRateHz.ToString();
        _suppressEvents = false;

        HmMappingGrid.ItemsSource = _mappingRows;
        LoadMappingRows(_settings.HmButtonMap);

        // Picks up driver installs/reinstalls triggered from the Status tab's Install Driver
        // button - RetryHidMaestro() there fires StatusChanged, but this view otherwise has no
        // way to know the driver state it cached at construction time is now stale.
        _engine.StatusChanged += () => Dispatcher.Invoke(RefreshHmDriverStatus);

        RefreshHmDriverStatus();
    }

    private void LoadMappingRows(Dictionary<HmSourceButton, HmOutputButton> map)
    {
        _mappingRows.Clear();
        foreach (var source in HmMapping.SourceOrder)
        {
            HmOutputButton dest = map.TryGetValue(source, out var mapped) ? mapped : HmOutputButton.None;
            _mappingRows.Add(new HmMappingRow(source, HmMapping.DisplayName(source), HmMapping.DisplayName(dest)));
        }
    }

    /// <summary>The mapping table's "Mapped To" column uses a plain ComboBox in a
    /// DataGridTemplateColumn (not DataGridComboBoxColumn) specifically so selection changes
    /// are written here immediately via a real user-gesture event, instead of depending on
    /// DataGridComboBoxColumn's SelectedItemBinding - that binding did not reliably push the
    /// user's pick back into the row even with Mode=TwoWay set (the app's custom dark-mode
    /// ComboBox template appears to interact badly with DataGridComboBoxColumn's editing
    /// lifecycle specifically; the same custom template works fine as a plain in-cell control).</summary>
    private void MappingComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string selected, DataContext: HmMappingRow row })
            row.SelectedOutputDisplay = selected;
    }

    private void HmConnectRadio_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.HmConnectMode = HmOnConnectRadio.IsChecked == true
            ? HmConnectMode.OnControllerConnect
            : HmConnectMode.OnStart;
        _settings.Save();
    }

    private void HmDisconnectOnControllerDisconnectCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.HmDisconnectOnControllerDisconnect = HmDisconnectOnControllerDisconnectCheckBox.IsChecked == true;
        _settings.Save();
    }

    private void HmSubmitRateSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HmSubmitRateValueText is null) return;
        int hz = (int)e.NewValue;
        HmSubmitRateValueText.Text = hz.ToString();
        if (_suppressEvents) return;
        _settings.HmSubmitRateHz = hz;
        _settings.Save();
        _engine.RefreshHmSubmitRate();
    }

    private void RefreshHmDriverStatus()
    {
        bool installed = HidMaestroOutput.CheckIsDriverInstalled();
        HmDriverDot.Fill              = installed ? SuccessBrush : ErrorBrush;
        HmDriverStatusText.Foreground = installed ? SuccessBrush : ErrorBrush;
        HmDriverStatusText.Text       = installed
            ? "Driver installed."
            : "Driver not installed - click Reinstall Drivers below.";
    }

    private async void HmReinstallButton_OnClick(object sender, RoutedEventArgs e)
    {
        HmReinstallButton.IsEnabled = false;
        HmDriverStatusText.Foreground = NeutralBrush;
        HmDriverStatusText.Text = "Installing...";

        // InstallDriver() evicts every bound devnode itself - stop our own controller first so
        // its submit thread isn't still writing to the device out from under that eviction.
        _engine.StopHidMaestroForDriverMaintenance();

        var (success, error) = await Task.Run(App.InstallHidMaestroDriver);
        HmReinstallButton.IsEnabled = true;

        if (!success)
        {
            HmDriverStatusText.Foreground = ErrorBrush;
            HmDriverStatusText.Text = $"Driver install failed: {error}";
            return;
        }

        RefreshHmDriverStatus();

        // If the engine is running with HIDMaestro selected and wasn't started yet, retry now.
        if (_settings.Backend == VirtualBackend.HidMaestro && _engine.IsRunning)
            _engine.RetryHidMaestro();
    }

    private async void HmUninstallButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will remove the HIDMaestro driver from Windows. Pangu Bridge won't be able to " +
            "create a DualSense Edge virtual controller until it's reinstalled. Continue?",
            "Uninstall HIDMaestro Driver", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        HmReinstallButton.IsEnabled = false;
        HmUninstallButton.IsEnabled = false;
        HmDriverStatusText.Foreground = NeutralBrush;
        HmDriverStatusText.Text = "Uninstalling...";

        // Stop our own controller first - RemoveAllVirtualControllers() evicts every bound
        // devnode, and if our submit thread is still writing to it when that happens, the
        // process crashes instead of finding a clean stopped state.
        _engine.StopHidMaestroForDriverMaintenance();

        var (success, error) = await Task.Run(App.UninstallHidMaestroDriver);
        HmReinstallButton.IsEnabled = true;
        HmUninstallButton.IsEnabled = true;

        if (!success)
        {
            HmDriverStatusText.Foreground = ErrorBrush;
            HmDriverStatusText.Text = $"Driver uninstall failed: {error}";
            return;
        }

        RefreshHmDriverStatus();
    }

    private void HmSaveMappingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var map = new Dictionary<HmSourceButton, HmOutputButton>();
        foreach (var row in _mappingRows)
            map[row.Source] = HmMapping.OutputOrder.First(o => HmMapping.DisplayName(o) == row.SelectedOutputDisplay);

        _settings.HmButtonMap = map;
        _settings.Save();
        _engine.ApplyHmButtonMap(map);

        MessageBox.Show("Mapping saved and applied.", "Pangu Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void HmResetMappingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will reset all HIDMaestro button mappings to their defaults and apply them immediately. Continue?",
            "Reset to Defaults", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var defaultMap = HmMapping.CreateDefault();
        LoadMappingRows(defaultMap);

        _settings.HmButtonMap = defaultMap;
        _settings.Save();
        _engine.ApplyHmButtonMap(defaultMap);
    }
}

/// <summary>One row of the HIDMaestro mapping table - Source/SourceDisplay are fixed,
/// SelectedOutputDisplay is the ComboBox-bound current choice.</summary>
public sealed class HmMappingRow : INotifyPropertyChanged
{
    // Shared across every row's ComboBox - instance property (rather than static) so it's
    // reachable from the DataGridTemplateColumn's per-row {Binding OutputChoices}, which
    // resolves against the row (this class), not the view.
    private static readonly IReadOnlyList<string> OutputChoicesList =
        HmMapping.OutputOrder.Select(HmMapping.DisplayName).ToList();
    public IReadOnlyList<string> OutputChoices => OutputChoicesList;

    public HmSourceButton Source { get; }
    public string SourceDisplay { get; }

    private string _selectedOutputDisplay;
    public string SelectedOutputDisplay
    {
        get => _selectedOutputDisplay;
        set
        {
            if (_selectedOutputDisplay == value) return;
            _selectedOutputDisplay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOutputDisplay)));
        }
    }

    public HmMappingRow(HmSourceButton source, string sourceDisplay, string selectedOutputDisplay)
    {
        Source = source;
        SourceDisplay = sourceDisplay;
        _selectedOutputDisplay = selectedOutputDisplay;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
