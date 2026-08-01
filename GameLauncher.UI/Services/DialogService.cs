using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GameLauncher.UI.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GameLauncher.UI.Services;

public interface IDialogService
{
    Task<bool> ShowConfirmAsync(string title, string message);
    Task ShowMessageAsync(string title, string message);
    Task<string?> ShowFolderPickerAsync(string title, string? initialPath = null);
    Task<string?> ShowFilePickerAsync(string title, string? initialPath = null);
}

public class DialogService : IDialogService
{
    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var window = GetMainWindow();
        if (window == null) return false;

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var cancelButton = new Button { Content = "Cancel" };
        var confirmButton = new Button { Content = "Confirm", Classes = { "Accent" } };

        cancelButton.Click += (_, _) => dialog.Close(false);
        confirmButton.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancelButton, confirmButton }
                }
            }
        };

        return await dialog.ShowDialog<bool>(window);
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var window = GetMainWindow();
        if (window == null) return;

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var okButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                okButton
            }
        };

        await dialog.ShowDialog(window);
    }

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