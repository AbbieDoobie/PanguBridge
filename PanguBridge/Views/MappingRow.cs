using System.ComponentModel;
using PanguBridge.Mapping;

namespace PanguBridge.Views;

public sealed class MappingRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public SourceButton SourceButton { get; }

    private string _vjoyButtonText;

    public MappingRow(SourceButton sourceButton, int? vjoyButton)
    {
        SourceButton = sourceButton;
        _vjoyButtonText = vjoyButton?.ToString() ?? string.Empty;
    }

    public string VJoyButtonText
    {
        get => _vjoyButtonText;
        set
        {
            if (_vjoyButtonText == value) return;
            _vjoyButtonText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VJoyButtonText)));
        }
    }

    /// <returns>Null if the field is blank or unparsable (treated as unmapped).</returns>
    public int? ToVJoyButton() => int.TryParse(VJoyButtonText, out int value) ? value : null;
}
