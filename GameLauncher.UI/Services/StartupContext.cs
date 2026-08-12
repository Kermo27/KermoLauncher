using GameLauncher.Core.Models;
using GameLauncher.Core.Services;

namespace GameLauncher.UI.Services;

/// <summary>
/// State loaded before the UI comes up, which keeps I/O out of the DI container and makes the
/// theme and language known from the first frame without blocking the UI thread.
/// </summary>
public sealed class StartupContext
{
    public LocalDbService Db { get; }
    public AppSettings Settings { get; }

    private StartupContext(LocalDbService db, AppSettings settings)
    {
        Db = db;
        Settings = settings;
    }

    /// <summary>Context backed by a throwaway database, for checking the dependency graph in tests.</summary>
    public static StartupContext ForTesting(string dbPath) =>
        new(new LocalDbService(dbPath), new AppSettings());

    public static async Task<StartupContext> LoadAsync()
    {
        var db = new LocalDbService();
        AppSettings settings;
        try
        {
            await db.InitializeAsync();
            settings = await db.GetSettingsAsync();
        }
        catch
        {
            // A corrupted database must not block startup, so defaults are used instead.
            settings = new AppSettings();
        }

        return new StartupContext(db, settings);
    }
}
