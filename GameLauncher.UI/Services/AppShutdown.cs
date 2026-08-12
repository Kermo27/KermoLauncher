using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace GameLauncher.UI.Services;

/// <summary>
/// Pozwala serwisom poprosić o zamknięcie aplikacji bez sięgania po Environment.Exit,
/// który ubijał proces w trakcie zapisów do bazy i trwających pobrań.
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
