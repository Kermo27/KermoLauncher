using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GameLauncher.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}