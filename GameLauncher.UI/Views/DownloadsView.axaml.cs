using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GameLauncher.UI.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}