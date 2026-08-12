using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace GameLauncher.UI.Services;

/// <summary>
/// Lets services ask for shutdown without reaching for Environment.Exit, which used to kill
/// the process in the middle of database writes and running downloads.
/// </summary>
public interface IAppShutdown
{
    void RequestShutdown();
}

public sealed class AppShutdown : IAppShutdown
{
    public void RequestShutdown()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        });
    }
}
