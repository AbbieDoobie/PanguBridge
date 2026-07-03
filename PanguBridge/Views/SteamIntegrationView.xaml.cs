using System.Windows;
using System.Windows.Controls;
using PanguBridge.Steam;

namespace PanguBridge.Views;

public partial class SteamIntegrationView : UserControl
{
    private readonly PanguEngine _engine;

    public SteamIntegrationView(PanguEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        MappingPreviewText.Text = SteamMappingWriter.BuildMappingString(_engine.Profile);
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(MappingPreviewText.Text);
        ResultText.Text = "Copied to clipboard.";
    }
}
