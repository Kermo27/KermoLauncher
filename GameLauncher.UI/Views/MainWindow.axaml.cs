using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using GameLauncher.UI.ViewModels;

namespace GameLauncher.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnToastPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ToastItemViewModel toast })
        {
            toast.CloseCommand.Execute(null);
        }
    }
}
