using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PanguBridge.Mapping;

namespace PanguBridge.Views;

public partial class MappingEditor : UserControl
{
    private readonly PanguEngine _engine;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<MappingRow> _rows = new();

    public MappingEditor(PanguEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        MappingGrid.ItemsSource = _rows;
        LoadFromProfile(_engine.Profile);
    }

    private void LoadFromProfile(MappingProfile profile)
    {
        _rows.Clear();
        foreach (SourceButton button in Enum.GetValues<SourceButton>())
        {
            profile.ButtonMap.TryGetValue(button, out int? slot);
            _rows.Add(new MappingRow(button, slot));
        }
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        // See HidMaestroView.HmSaveMappingsButton_OnClick - a DataGrid cell's edit doesn't
        // push into the bound row object until the cell/row edit is committed, so a value
        // typed and then immediately followed by clicking Save (with no intervening Tab/
        // Enter) would otherwise be lost.
        MappingGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var profile = _engine.Profile;
        profile.ButtonMap.Clear();
        foreach (var row in _rows)
        {
            profile.ButtonMap[row.SourceButton] = row.ToVJoyButton();
        }

        // profile.ButtonMap is the same Dictionary instance as settings.VJoyButtonMap (set up
        // once in App.xaml.cs), so it's already reflected there - just persist it.
        _settings.Save();
        _engine.ApplyProfile(profile);
        MessageBox.Show("Mapping saved.", "Pangu Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        LoadFromProfile(MappingProfile.CreateDefault());
    }
}
