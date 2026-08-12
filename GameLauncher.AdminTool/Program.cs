using Avalonia;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services;

namespace GameLauncher.AdminTool;

class Program
{
    public static int Main(string[] args)
    {
        // Ustawienia czytamy przed startem Avalonii — w Main nie ma jeszcze pętli komunikatów,
        // więc oczekiwanie nie może się zakleszczyć, a wątek UI nie czeka na dysk.
        App.Startup = LoadSettingsAsync().GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<(LocalDbService Db, AppSettings Settings)> LoadSettingsAsync()
    {
        var db = new LocalDbService();
        try
        {
            await db.InitializeAsync();
            return (db, await db.GetSettingsAsync());
        }
        catch
        {
            return (db, new AppSettings());
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
