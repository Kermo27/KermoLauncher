using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GameLauncher.AdminTool.Views;

public partial class UploadView : UserControl
{
    public UploadView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}