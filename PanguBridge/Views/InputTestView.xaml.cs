using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PanguBridge.Controllers;

namespace PanguBridge.Views;

public partial class InputTestView : UserControl
{
    // ~20 Hz: plenty legible for a human-readable table, nowhere close to trying to keep up
    // with the actual input/submit pipeline (which this timer never touches at all - it only
    // ever reads HidReader.CurrentState, a plain snapshot property, from the UI thread).
    private const int RefreshIntervalMs = 50;

    private readonly PanguEngine _engine;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<InputRow> _rows = new();
    private DispatcherTimer? _refreshTimer;

    // Named references into _rows so RefreshTable can update values directly without any
    // lookup - populated once in the constructor, in the same order they're added below.
    private readonly InputRow _leftStick, _rightStick, _leftTrigger, _rightTrigger;
    private readonly InputRow _dpadUp, _dpadDown, _dpadLeft, _dpadRight;
    private readonly InputRow _a, _b, _x, _y;
    private readonly InputRow _leftShoulder, _rightShoulder;
    private readonly InputRow _leftThumbClick, _rightThumbClick;
    private readonly InputRow _start, _back;
    private readonly InputRow _ai, _fn, _profile;
    private readonly InputRow _m1, _m2, _m3, _m4;
    private readonly InputRow _lm, _rm;

    public InputTestView(PanguEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;

        _rows.Add(_leftStick     = new InputRow("Left Stick"));
        _rows.Add(_rightStick    = new InputRow("Right Stick"));
        _rows.Add(_leftTrigger   = new InputRow("Left Trigger"));
        _rows.Add(_rightTrigger  = new InputRow("Right Trigger"));
        _rows.Add(_dpadUp        = new InputRow("D-Pad Up"));
        _rows.Add(_dpadDown      = new InputRow("D-Pad Down"));
        _rows.Add(_dpadLeft      = new InputRow("D-Pad Left"));
        _rows.Add(_dpadRight     = new InputRow("D-Pad Right"));
        _rows.Add(_a             = new InputRow("A"));
        _rows.Add(_b             = new InputRow("B"));
        _rows.Add(_x             = new InputRow("X"));
        _rows.Add(_y             = new InputRow("Y"));
        _rows.Add(_leftShoulder  = new InputRow("Left Shoulder"));
        _rows.Add(_rightShoulder = new InputRow("Right Shoulder"));
        _rows.Add(_leftThumbClick  = new InputRow("Left Thumb Click"));
        _rows.Add(_rightThumbClick = new InputRow("Right Thumb Click"));
        _rows.Add(_start         = new InputRow("Start"));
        _rows.Add(_back          = new InputRow("Back"));
        _rows.Add(_ai            = new InputRow("AI"));
        _rows.Add(_fn            = new InputRow("FN"));
        _rows.Add(_profile       = new InputRow("Profile"));
        _rows.Add(_m1            = new InputRow("M1"));
        _rows.Add(_m2            = new InputRow("M2"));
        _rows.Add(_m3            = new InputRow("M3"));
        _rows.Add(_m4            = new InputRow("M4"));
        _rows.Add(_lm            = new InputRow("LM"));
        _rows.Add(_rm            = new InputRow("RM"));
        InputGrid.ItemsSource = _rows;

        _engine.StatusChanged += () => Dispatcher.Invoke(Refresh);
        Refresh();
    }

    private void InputTestView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_refreshTimer != null) return;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs),
        };
        _refreshTimer.Tick += (_, _) => RefreshTable();
        _refreshTimer.Start();
        RefreshTable();
    }

    private void InputTestView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    private void RefreshTable()
    {
        var s = _engine.HidReader.CurrentState;

        _leftStick.Value    = $"{s.LeftStickX}, {s.LeftStickY}";
        _rightStick.Value   = $"{s.RightStickX}, {s.RightStickY}";
        _leftTrigger.Value  = s.LeftTrigger.ToString();
        _rightTrigger.Value = s.RightTrigger.ToString();

        SetButton(_dpadUp, s.DPadUp);
        SetButton(_dpadDown, s.DPadDown);
        SetButton(_dpadLeft, s.DPadLeft);
        SetButton(_dpadRight, s.DPadRight);
        SetButton(_a, s.A);
        SetButton(_b, s.B);
        SetButton(_x, s.X);
        SetButton(_y, s.Y);
        SetButton(_leftShoulder, s.LeftShoulder);
        SetButton(_rightShoulder, s.RightShoulder);
        SetButton(_leftThumbClick, s.LeftThumbClick);
        SetButton(_rightThumbClick, s.RightThumbClick);
        SetButton(_start, s.Start);
        SetButton(_back, s.Back);
        SetButton(_ai, s.Ai);
        SetButton(_fn, s.Fn);
        SetButton(_profile, s.Profile);
        SetButton(_m1, s.M1);
        SetButton(_m2, s.M2);
        SetButton(_m3, s.M3);
        SetButton(_m4, s.M4);
        SetButton(_lm, s.Lm);
        SetButton(_rm, s.Rm);
    }

    private static void SetButton(InputRow row, bool pressed)
    {
        row.IsActive = pressed;
        row.Value = pressed ? "1" : "0";
    }

    private void Refresh()
    {
        bool enabled = _engine.IsRunning && _engine.HidReader.IsConnected;
        SetVibrationTestButtonsEnabled(enabled);
    }

    private void SetVibrationTestButtonsEnabled(bool enabled)
    {
        TestLeftGripButton.IsEnabled = enabled;
        TestRightGripButton.IsEnabled = enabled;
        TestLeftTriggerButton.IsEnabled = enabled;
        TestRightTriggerButton.IsEnabled = enabled;
    }

    private void TestLeftGripButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_engine.HidReader.IsConnected) return;
        TestLeftGripButton.IsEnabled = false;
        _ = RunGripTestAsync(isLeft: true);
    }

    private void TestRightGripButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_engine.HidReader.IsConnected) return;
        TestRightGripButton.IsEnabled = false;
        _ = RunGripTestAsync(isLeft: false);
    }

    // Scaled by the same Rumble Intensity cap the live rumble path uses (PanguEngine.
    // ScaleGripIntensity), so the test reflects what you'd actually feel, not raw/uncapped
    // intensity.
    private async Task RunGripTestAsync(bool isLeft)
    {
        int capPercent = isLeft ? _settings.GripLeftIntensity : _settings.GripRightIntensity;
        try
        {
            for (int i = 0; i <= 255; i += 15)
            {
                byte scaled = PanguEngine.ScaleGripIntensity((byte)i, capPercent);
                _engine.HidReader.TrySendMotors(isLeft ? scaled : (byte)0, isLeft ? (byte)0 : scaled, 0, 0);
                await Task.Delay(40);
            }
            for (int i = 255; i >= 0; i -= 15)
            {
                byte scaled = PanguEngine.ScaleGripIntensity((byte)i, capPercent);
                _engine.HidReader.TrySendMotors(isLeft ? scaled : (byte)0, isLeft ? (byte)0 : scaled, 0, 0);
                await Task.Delay(40);
            }
            _engine.HidReader.TrySendMotors(0, 0, 0, 0);
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                if (isLeft) TestLeftGripButton.IsEnabled = true;
                else TestRightGripButton.IsEnabled = true;
            });
        }
    }

    private void TestLeftTriggerButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_engine.HidReader.IsConnected) return;
        TestLeftTriggerButton.IsEnabled = false;
        _ = RunTriggerTestAsync(isLeft: true);
    }

    private void TestRightTriggerButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_engine.HidReader.IsConnected) return;
        TestRightTriggerButton.IsEnabled = false;
        _ = RunTriggerTestAsync(isLeft: false);
    }

    // Ramp is clamped to the configured Rumble Intensity cap for that side's trigger, so
    // e.g. a cap of 2 shows the ramp topping out at level 2 instead of climbing to 4.
    private async Task RunTriggerTestAsync(bool isLeft)
    {
        int cap = isLeft ? _settings.TriggerLeftIntensity : _settings.TriggerRightIntensity;
        try
        {
            for (int level = 0; level <= 4; level++)
            {
                int scaled = Math.Min(level, cap);
                _engine.HidReader.TrySendMotors(0, 0, isLeft ? scaled : 0, isLeft ? 0 : scaled);
                await Task.Delay(400);
            }
            for (int level = 4; level >= 0; level--)
            {
                int scaled = Math.Min(level, cap);
                _engine.HidReader.TrySendMotors(0, 0, isLeft ? scaled : 0, isLeft ? 0 : scaled);
                await Task.Delay(400);
            }
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                TestLeftTriggerButton.IsEnabled = true;
                TestRightTriggerButton.IsEnabled = true;
            });
        }
    }
}

/// <summary>One row of the live input table - Name is fixed, Value/IsActive update in place
/// each refresh tick so the DataGrid never needs to rebuild its item list.</summary>
public sealed class InputRow : INotifyPropertyChanged
{
    public string Name { get; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public InputRow(string name) => Name = name;

    public event PropertyChangedEventHandler? PropertyChanged;
}
