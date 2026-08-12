using Avalonia;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services;

namespace GameLauncher.AdminTool;

class Program
{
    public static int Main(string[] args)
    {
        // Settings are read before Avalonia starts: Main has no message loop yet, so blocking
        // here cannot deadlock and the UI thread never waits on the disk.
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
