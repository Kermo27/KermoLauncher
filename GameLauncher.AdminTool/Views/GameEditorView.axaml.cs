using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GameLauncher.AdminTool.Views;

public partial class GameEditorView : UserControl
{
    public GameEditorView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}