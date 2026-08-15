using System.Diagnostics;

namespace GameLauncher.UI.Services;

/// <summary>
/// Opens a folder or file with the desktop shell. Isolated so ViewModels can be tested
/// without spawning explorer / xdg-open.
/// </summary>
public interface IShellService
{
    void OpenFolder(string path);
    void OpenFile(string path);
}

public sealed class DesktopShellService : IShellService
{
    public void OpenFolder(string path) => Open(path, directory: true);

    public void OpenFile(string path) => Open(path, directory: false);

    private static void Open(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (directory)
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
                FileName = path,
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
