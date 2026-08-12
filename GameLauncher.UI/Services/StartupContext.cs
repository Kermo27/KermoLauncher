using GameLauncher.Core.Models;
using GameLauncher.Core.Services;

namespace GameLauncher.UI.Services;

/// <summary>
/// Stan wczytany zanim wstanie interfejs. Dzięki temu kontener DI nie wykonuje wejścia/wyjścia,
/// a motyw i język są znane od pierwszej klatki — bez blokowania wątku UI.
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

    /// <summary>Kontekst na bazie tymczasowej, do sprawdzania grafu zależności w testach.</summary>
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
            // Uszkodzona baza nie może blokować startu — lecimy na domyślnych ustawieniach.
            settings = new AppSettings();
        }

        return new StartupContext(db, settings);
    }
}
