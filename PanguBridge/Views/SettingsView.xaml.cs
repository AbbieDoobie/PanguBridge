using System.Windows;
using System.Windows.Controls;
using PanguBridge.Controllers;

namespace PanguBridge.Views;

public partial class SettingsView : UserControl
{
    private readonly PanguEngine _engine;
    private readonly AppSettings _settings;
    private bool _suppressEvents;

    public SettingsView(PanguEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;

        _suppressEvents = true;
        (settings.Backend == VirtualBackend.VJoy ? VJoyRadio : HidMaestroRadio).IsChecked = true;
        (settings.Theme == "Light" ? LightThemeRadio : DarkThemeRadio).IsChecked = true;
        AutostartCheckBox.IsChecked         = Autostart.IsEnabled();
        SwapTriggersCheckBox.IsChecked      = _settings.SwapTriggers;
        SwapSticksCheckBox.IsChecked        = _settings.SwapSticks;
        InvertLeftStickXCheckBox.IsChecked  = _settings.InvertLeftStickX;
        InvertLeftStickYCheckBox.IsChecked  = _settings.InvertLeftStickY;
        InvertRightStickXCheckBox.IsChecked = _settings.InvertRightStickX;
        InvertRightStickYCheckBox.IsChecked = _settings.InvertRightStickY;

        RumbleModeRadioFor(settings.RumbleMode).IsChecked = true;
        GripLeftSlider.Value     = _settings.GripLeftIntensity;
        GripRightSlider.Value    = _settings.GripRightIntensity;
        TriggerLeftSlider.Value  = _settings.TriggerLeftIntensity;
        TriggerRightSlider.Value = _settings.TriggerRightIntensity;
        GripLeftValueText.Text     = _settings.GripLeftIntensity.ToString();
        GripRightValueText.Text    = _settings.GripRightIntensity.ToString();
        TriggerLeftValueText.Text  = _settings.TriggerLeftIntensity.ToString();
        TriggerRightValueText.Text = _settings.TriggerRightIntensity.ToString();

        AudioAutoHapticsEnabledCheckBox.IsChecked = _settings.AudioAutoHapticsEnabled;
        AudioGripModeComboBox.ItemsSource = AudioModeDisplayNames;
        AudioTriggerPulledModeComboBox.ItemsSource = AudioModeDisplayNames;
        AudioTriggerIdleModeComboBox.ItemsSource = AudioModeDisplayNames;
        AudioGripModeComboBox.SelectedItem = AudioModeDisplayName(_settings.AudioAutoHapticsGripMode);
        AudioTriggerPulledModeComboBox.SelectedItem = AudioModeDisplayName(_settings.AudioAutoHapticsTriggerPulledMode);
        AudioTriggerIdleModeComboBox.SelectedItem = AudioModeDisplayName(_settings.AudioAutoHapticsTriggerIdleMode);
        PopulateAudioDeviceList();
        AudioCutoffSlider.Value  = _settings.AudioAutoHapticsCutoffHz;
        AudioAttackSlider.Value  = _settings.AudioAutoHapticsAttackMs;
        AudioReleaseSlider.Value = _settings.AudioAutoHapticsReleaseMs;
        AudioBoostSlider.Value   = _settings.AudioAutoHapticsIntensityBoost;
        AudioGripNoiseFloorSlider.Value          = _settings.AudioAutoHapticsGripNoiseFloor;
        AudioTriggerPulledNoiseFloorSlider.Value = _settings.AudioAutoHapticsTriggerPulledNoiseFloor;
        AudioTriggerIdleNoiseFloorSlider.Value   = _settings.AudioAutoHapticsTriggerIdleNoiseFloor;
        AudioCutoffValueText.Text  = $"{_settings.AudioAutoHapticsCutoffHz:0} Hz";
        AudioAttackValueText.Text  = $"{_settings.AudioAutoHapticsAttackMs:0.0} ms";
        AudioReleaseValueText.Text = $"{_settings.AudioAutoHapticsReleaseMs:0} ms";
        AudioBoostValueText.Text   = $"{_settings.AudioAutoHapticsIntensityBoost:0.0}x";
        AudioGripNoiseFloorValueText.Text          = $"{_settings.AudioAutoHapticsGripNoiseFloor:0}%";
        AudioTriggerPulledNoiseFloorValueText.Text = $"{_settings.AudioAutoHapticsTriggerPulledNoiseFloor:0}%";
        AudioTriggerIdleNoiseFloorValueText.Text   = $"{_settings.AudioAutoHapticsTriggerIdleNoiseFloor:0}%";
        AudioIncludeLfeCheckBox.IsChecked = _settings.AudioAutoHapticsIncludeLfe;
        AudioIncludeCenterCheckBox.IsChecked = _settings.AudioAutoHapticsIncludeCenter;

        AdaptiveTriggerSimulationCheckBox.IsChecked = _settings.AdaptiveTriggerSimulation;
        AdaptiveTriggerIgnoreIntensityCheckBox.IsChecked = _settings.AdaptiveTriggerIgnoreIntensity;
        AdaptiveTriggerDisableMatchingGripCheckBox.IsChecked = _settings.AdaptiveTriggerDisableMatchingGrip;
        _suppressEvents = false;
    }

    private void BackendRadio_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Backend = VJoyRadio.IsChecked == true ? VirtualBackend.VJoy : VirtualBackend.HidMaestro;
        _settings.Save();
    }

    private void ThemeRadio_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Theme = LightThemeRadio.IsChecked == true ? "Light" : "Dark";
        _settings.Save();
        ThemeManager.Apply(_settings.Theme);
    }

    private void AutostartCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        bool wantEnabled = AutostartCheckBox.IsChecked == true;
        string? error;
        bool ok = wantEnabled ? Autostart.Enable(out error) : Autostart.Disable(out error);

        if (ok) return;

        MessageBox.Show(
            $"Failed to {(wantEnabled ? "enable" : "disable")} autostart:\n\n{error}",
            "Pangu Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);

        // Revert the checkbox to reflect what's actually registered, not what the user
        // just clicked - the change didn't take effect.
        _suppressEvents = true;
        AutostartCheckBox.IsChecked = Autostart.IsEnabled();
        _suppressEvents = false;
    }

    private void SwapTriggersCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.SwapTriggers = SwapTriggersCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAnalogAdjustments();
    }

    private void SwapSticksCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.SwapSticks = SwapSticksCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAnalogAdjustments();
    }

    private void InvertLeftStickXCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InvertLeftStickX = InvertLeftStickXCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAnalogAdjustments();
    }

    private void InvertLeftStickYCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InvertLeftStickY = InvertLeftStickYCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAnalogAdjustments();
    }

    private void InvertRightStickXCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InvertRightStickX = InvertRightStickXCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAnalogAdjustments();
    }

    private void InvertRightStickYCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InvertRightStickY = InvertRightStickYCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAnalogAdjustments();
    }

    private RadioButton RumbleModeRadioFor(RumbleMode mode) => mode switch
    {
        RumbleMode.TriggerOnly              => TriggerOnlyRadio,
        RumbleMode.GripAndTrigger           => GripAndTriggerRadio,
        RumbleMode.GripAndTriggerIfPulled   => GripAndTriggerIfPulledRadio,
        RumbleMode.GripOrTriggerIfPulled    => GripOrTriggerIfPulledRadio,
        _                                   => GripOnlyRadio,
    };

    private void RumbleModeRadio_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.RumbleMode =
            TriggerOnlyRadio.IsChecked == true              ? RumbleMode.TriggerOnly :
            GripAndTriggerRadio.IsChecked == true            ? RumbleMode.GripAndTrigger :
            GripAndTriggerIfPulledRadio.IsChecked == true    ? RumbleMode.GripAndTriggerIfPulled :
            GripOrTriggerIfPulledRadio.IsChecked == true     ? RumbleMode.GripOrTriggerIfPulled :
            RumbleMode.GripOnly;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void GripLeftSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        GripLeftValueText.Text = ((int)e.NewValue).ToString();
        if (_suppressEvents) return;
        _settings.GripLeftIntensity = (int)e.NewValue;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void GripRightSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        GripRightValueText.Text = ((int)e.NewValue).ToString();
        if (_suppressEvents) return;
        _settings.GripRightIntensity = (int)e.NewValue;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void TriggerLeftSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TriggerLeftValueText.Text = ((int)e.NewValue).ToString();
        if (_suppressEvents) return;
        _settings.TriggerLeftIntensity = (int)e.NewValue;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void TriggerRightSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TriggerRightValueText.Text = ((int)e.NewValue).ToString();
        if (_suppressEvents) return;
        _settings.TriggerRightIntensity = (int)e.NewValue;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private static readonly (AudioAutoHapticsMode Mode, string Display)[] AudioModes =
    {
        (AudioAutoHapticsMode.NoAudioRumble, "No Audio Rumble"),
        (AudioAutoHapticsMode.Replace, "Replace with Audio Rumble"),
        (AudioAutoHapticsMode.NormalPlusAudio, "Normal + Audio (Normalized)"),
    };

    private static readonly string[] AudioModeDisplayNames = AudioModes.Select(m => m.Display).ToArray();

    private static string AudioModeDisplayName(AudioAutoHapticsMode mode) =>
        AudioModes.First(m => m.Mode == mode).Display;

    private static AudioAutoHapticsMode AudioModeFromDisplayName(string display) =>
        AudioModes.First(m => m.Display == display).Mode;

    private void PopulateAudioDeviceList()
    {
        var devices = _engine.AudioAutoHaptics.EnumerateOutputDevices();
        AudioDeviceComboBox.ItemsSource = devices;
        AudioDeviceComboBox.SelectedItem = devices.FirstOrDefault(d => d.Id == _settings.AudioAutoHapticsDeviceId)
            ?? devices[0];
    }

    private void AudioAutoHapticsEnabledCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsEnabled = AudioAutoHapticsEnabledCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAudioAutoHapticsCapture();
    }

    private void AudioDeviceComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (AudioDeviceComboBox.SelectedItem is not AudioDeviceOption option) return;
        _settings.AudioAutoHapticsDeviceId = option.Id;
        _settings.Save();
        _engine.RefreshAudioAutoHapticsCapture();
    }

    private void RefreshAudioDevicesButton_OnClick(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        PopulateAudioDeviceList();
        _suppressEvents = false;
    }

    private void AudioGripModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (AudioGripModeComboBox.SelectedItem is not string display) return;
        _settings.AudioAutoHapticsGripMode = AudioModeFromDisplayName(display);
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void AudioTriggerPulledModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (AudioTriggerPulledModeComboBox.SelectedItem is not string display) return;
        _settings.AudioAutoHapticsTriggerPulledMode = AudioModeFromDisplayName(display);
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void AudioTriggerIdleModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (AudioTriggerIdleModeComboBox.SelectedItem is not string display) return;
        _settings.AudioAutoHapticsTriggerIdleMode = AudioModeFromDisplayName(display);
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    // Cutoff/Attack/Release/Boost (unlike Grip/Trigger Intensity above) set a nonzero XAML
    // Minimum, so WPF coerces Value up to it the moment Minimum is parsed - which fires
    // ValueChanged while InitializeComponent() is still connecting later-declared named
    // elements, before this handler's own ValueText field exists yet. The null check guards
    // specifically against that load-time firing, not against anything at runtime. The three
    // Noise Floor sliders below have a zero Minimum and don't strictly need the guard, but keep
    // it anyway for consistency with the rest of this Advanced section.
    private void AudioCutoffSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioCutoffValueText is null) return;
        AudioCutoffValueText.Text = $"{e.NewValue:0} Hz";
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsCutoffHz = e.NewValue;
        _settings.Save();
        _engine.RefreshAudioAutoHapticsTuning();
    }

    private void AudioIncludeLfeCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsIncludeLfe = AudioIncludeLfeCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAudioAutoHapticsTuning();
    }

    private void AudioIncludeCenterCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsIncludeCenter = AudioIncludeCenterCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshAudioAutoHapticsTuning();
    }

    private void AudioAttackSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioAttackValueText is null) return;
        AudioAttackValueText.Text = $"{e.NewValue:0.0} ms";
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsAttackMs = e.NewValue;
        _settings.Save();
        _engine.RefreshAudioAutoHapticsTuning();
    }

    private void AudioReleaseSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioReleaseValueText is null) return;
        AudioReleaseValueText.Text = $"{e.NewValue:0} ms";
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsReleaseMs = e.NewValue;
        _settings.Save();
        _engine.RefreshAudioAutoHapticsTuning();
    }

    private void AudioBoostSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioBoostValueText is null) return;
        AudioBoostValueText.Text = $"{e.NewValue:0.0}x";
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsIntensityBoost = e.NewValue;
        _settings.Save();
        _engine.RefreshAudioAutoHapticsTuning();
    }

    private void AudioGripNoiseFloorSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioGripNoiseFloorValueText is null) return;
        AudioGripNoiseFloorValueText.Text = $"{e.NewValue:0}%";
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsGripNoiseFloor = e.NewValue;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void AudioTriggerPulledNoiseFloorSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioTriggerPulledNoiseFloorValueText is null) return;
        AudioTriggerPulledNoiseFloorValueText.Text = $"{e.NewValue:0}%";
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsTriggerPulledNoiseFloor = e.NewValue;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void AudioTriggerIdleNoiseFloorSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioTriggerIdleNoiseFloorValueText is null) return;
        AudioTriggerIdleNoiseFloorValueText.Text = $"{e.NewValue:0}%";
        if (_suppressEvents) return;
        _settings.AudioAutoHapticsTriggerIdleNoiseFloor = e.NewValue;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void AdaptiveTriggerSimulationCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AdaptiveTriggerSimulation = AdaptiveTriggerSimulationCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void AdaptiveTriggerIgnoreIntensityCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AdaptiveTriggerIgnoreIntensity = AdaptiveTriggerIgnoreIntensityCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }

    private void AdaptiveTriggerDisableMatchingGripCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AdaptiveTriggerDisableMatchingGrip = AdaptiveTriggerDisableMatchingGripCheckBox.IsChecked == true;
        _settings.Save();
        _engine.RefreshRumbleSettings();
    }
}
