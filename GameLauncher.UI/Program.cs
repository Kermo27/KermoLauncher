using Avalonia;
using GameLauncher.UI.Services;

namespace GameLauncher.UI;

class Program
{
    public static int Main(string[] args)
    {
        // Ustawienia czytamy tutaj, przed uruchomieniem Avalonii: w Main nie ma jeszcze
        // pętli komunikatów ani kontekstu synchronizacji, więc oczekiwanie na zadanie
        // nie może się zakleszczyć — inaczej niż w OnFrameworkInitializationCompleted.
        App.Startup = StartupContext.LoadAsync().GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
