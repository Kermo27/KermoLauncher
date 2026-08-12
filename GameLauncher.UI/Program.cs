using Avalonia;
using GameLauncher.UI.Services;

namespace GameLauncher.UI;

class Program
{
    public static int Main(string[] args)
    {
        // Settings are read here, before Avalonia starts: Main has no message loop and no
        // synchronization context yet, so blocking on a task cannot deadlock the way it
        // would inside OnFrameworkInitializationCompleted.
        App.Startup = StartupContext.LoadAsync().GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
