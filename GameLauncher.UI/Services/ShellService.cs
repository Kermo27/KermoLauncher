using System.Diagnostics;

namespace GameLauncher.UI.Services;

/// <summary>
/// Opens a folder in the desktop file manager. Isolated so ViewModels can be tested
/// without spawning explorer / xdg-open.
/// </summary>
public interface IShellService
{
    void OpenFolder(string path);
}

public sealed class DesktopShellService : IShellService
{
    public void OpenFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { path },
                UseShellExecute = true
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "xdg-open",
            ArgumentList = { path },
            UseShellExecute = false
        });
    }
}
