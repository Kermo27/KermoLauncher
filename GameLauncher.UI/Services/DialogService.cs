using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GameLauncher.UI.Services;

public interface IDialogService
{
    Task<bool> ShowConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null);
    Task ShowMessageAsync(string title, string message, bool isError = false);
    Task<string?> ShowFolderPickerAsync(string title, string? initialPath = null);
    Task<string?> ShowFilePickerAsync(string title, string? initialPath = null);
}

public class DialogService : IDialogService
{
    private readonly ILocalizationService _l;

    public DialogService(ILocalizationService localization)
    {
        _l = localization;
    }

    public async Task<bool> ShowConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null)
    {
        var owner = GetMainWindow();
        if (owner == null) return false;

        var cancelButton = new Button { Content = cancelText ?? _l["Dialog.Cancel"], Classes = { "ghost" } };
        var confirmButton = new Button { Content = confirmText ?? _l["Dialog.Confirm"], Classes = { "primary" } };

        var dialog = BuildDialog(title, message, "\u26A0\uFE0F", "WarningBrush", cancelButton, confirmButton);

        cancelButton.Click += (_, _) => dialog.Close(false);
        confirmButton.Click += (_, _) => dialog.Close(true);

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.Close(false);
            }
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task ShowMessageAsync(string title, string message, bool isError = false)
    {
        var owner = GetMainWindow();
        if (owner == null) return;

        var okButton = new Button { Content = _l["Dialog.Ok"], Classes = { "primary" } };

        var dialog = BuildDialog(title, message, isError ? "\u274C" : "\u2139\uFE0F",
            isError ? "DangerBrush" : "AccentBrush", okButton);

        okButton.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.Close();
            }
        };

        await dialog.ShowDialog(owner);
    }

    private Window BuildDialog(string title, string message, string icon, string accentKey, params Button[] buttons)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        BindBrush(dialog, Window.BackgroundProperty, "WindowBgBrush");

        var iconText = new TextBlock
        {
            Text = icon,
            FontSize = 28,
            VerticalAlignment = VerticalAlignment.Top
        };
        BindBrush(iconText, TextBlock.ForegroundProperty, accentKey);

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        BindBrush(titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        iconText.Margin = new Thickness(0, 2, 12, 0);
        header.Children.Add(iconText);
        header.Children.Add(titleText);
        Grid.SetColumn(titleText, 1);

        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        BindBrush(messageText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 24, 0, 0)
        };
        foreach (var button in buttons)
        {
            footer.Children.Add(button);
        }

        var panel = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(messageText, Dock.Bottom);
        panel.Children.Add(footer);
        panel.Children.Add(messageText);
        panel.Children.Add(header);

        dialog.Content = panel;
        return dialog;
    }

    /// <summary>
    /// Theme brushes live in theme dictionaries, so they can only be resolved against the variant a
    /// control actually renders with. A one-off lookup misses them whenever the app follows the
    /// system theme, which left the dialog transparent and its text invisible.
    /// </summary>
    private static void BindBrush(AvaloniaObject target, AvaloniaProperty property, string resourceKey)
        => target.Bind(property, new DynamicResourceExtension(resourceKey));

    public async Task<string?> ShowFolderPickerAsync(string title, string? initialPath = null)
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var startLocation = initialPath != null
            ? await window.StorageProvider.TryGetFolderFromPathAsync(initialPath)
            : null;

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = startLocation,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    public async Task<string?> ShowFilePickerAsync(string title, string? initialPath = null)
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var dir = initialPath != null ? Path.GetDirectoryName(initialPath) : null;
        var startLocation = dir != null
            ? await window.StorageProvider.TryGetFolderFromPathAsync(dir)
            : null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = startLocation,
            AllowMultiple = false
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private static Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }
}
