using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace GameLauncher.AdminTool.Services;

public interface IFolderPicker
{
    Task<string?> PickAsync(string title, string? initialPath = null);
}

public sealed class FolderPicker : IFolderPicker
{
    public async Task<string?> PickAsync(string title, string? initialPath = null)
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

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
