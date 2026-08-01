using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GameLauncher.UI.ViewModels;

namespace GameLauncher.UI.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += async (_, _) => 
        {
            if (DataContext is LibraryViewModel vm)
            {
                await vm.EnsureLoadedAsync();
            }
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}