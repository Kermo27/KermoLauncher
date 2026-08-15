using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using GameLauncher.UI.ViewModels;

namespace GameLauncher.UI.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Opens details for the card. Clicks on action buttons are ignored so Install/Launch still work.
    /// </summary>
    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Control source && IsInsideButton(source)) return;
        if (sender is not Control { DataContext: GameItemViewModel item }) return;
        if (DataContext is not LibraryViewModel library) return;

        library.OpenDetails(item);
        e.Handled = true;
    }

    private static bool IsInsideButton(Control source)
    {
        Avalonia.Visual? current = source;
        while (current != null)
        {
            if (current is Button) return true;
            current = current.GetVisualParent();
        }

        return false;
    }
}
