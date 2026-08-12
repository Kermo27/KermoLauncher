namespace GameLauncher.Core.Services;

using System.Data;
using Dapper;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Data.Sqlite;
using System.Text.Json;

public class LocalDbService : ILocalDbService
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;
    private AppSettings? _settingsCache;

    public LocalDbService(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(Utils.AppPaths.DataDirectory, "launcher.db");
    }

    /// <summary>
    /// Opens a connection and turns foreign keys on. PRAGMA foreign_keys is scoped to a single
    /// connection, so it has to be set every time or ON DELETE CASCADE silently does nothing.
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            
            await using var conn = await OpenConnectionAsync();

            await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");

            await conn.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS games (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    version TEXT,
                    description TEXT,
                    tags TEXT,
                    dependencies TEXT,
                    screenshot_urls TEXT,
                    remote_zip_url TEXT,
                    size_bytes INTEGER,
                    sha256 TEXT,
                    launch_config TEXT
                );
            """);

            await conn.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS game_local_state (
                    game_id TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    installed_path TEXT,
                    play_time_seconds INTEGER DEFAULT 0,
                    last_played INTEGER,
                    FOREIGN KEY (game_id) REFERENCES games(id) ON DELETE CASCADE
                );
            """);

            await conn.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS downloads (
                    id TEXT PRIMARY KEY,
                    game_id TEXT NOT NULL,
                    remote_url TEXT NOT NULL,
                    local_path TEXT NOT NULL,
                    total_bytes INTEGER NOT NULL,
                    downloaded_bytes INTEGER DEFAULT 0,
                    status TEXT NOT NULL,
                    error TEXT,
                    started_at INTEGER,
                    completed_at INTEGER,
                    FOREIGN KEY (game_id) REFERENCES games(id) ON DELETE CASCADE
                );
            """);

            await conn.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
            """);

            await MigrateSchemaAsync(conn);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task MigrateSchemaAsync(SqliteConnection conn)
    {
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );
        """);

        var versionRow = await conn.QueryFirstOrDefaultAsync<int>("SELECT COALESCE(MAX(version), 0) FROM schema_version");
        if (versionRow < 2)
        {
            await AddColumnIfMissingAsync(conn, "games", "manifest_url", "TEXT");
            await AddColumnIfMissingAsync(conn, "game_local_state", "installed_version", "TEXT");
            await AddColumnIfMissingAsync(conn, "game_local_state", "installed_manifest", "TEXT");
            await conn.ExecuteAsync("INSERT INTO schema_version (version) VALUES (2)");
        }
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection conn, string table, string column, string type)
    {
        var cols = await conn.QueryAsync<string>($"PRAGMA table_info({table})");
        if (!cols.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)))
        {
            await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type}");
        }
    }

    public async Task UpsertGamesAsync(Game[] games)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();
        
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var game in games)
            {
                var tagsJson = JsonSerializer.Serialize(game.Tags);
                var depsJson = JsonSerializer.Serialize(game.Dependencies);
                var screenshotsJson = JsonSerializer.Serialize(game.ScreenshotUrls);
                var launchConfigJson = game.LaunchConfig != null ? JsonSerializer.Serialize(game.LaunchConfig) : null;

                await conn.ExecuteAsync("""
                    INSERT INTO games (id, name, version, description, tags, dependencies, screenshot_urls, manifest_url, size_bytes, launch_config)
                    VALUES (@Id, @Name, @Version, @Description, @Tags, @Dependencies, @ScreenshotUrls, @ManifestUrl, @SizeBytes, @LaunchConfig)
                    ON CONFLICT(id) DO UPDATE SET
                        name=@Name, version=@Version, description=@Description,
                        tags=@Tags, dependencies=@Dependencies, screenshot_urls=@ScreenshotUrls,
                        manifest_url=@ManifestUrl, size_bytes=@SizeBytes, launch_config=@LaunchConfig
                """, new
                {
                    game.Id,
                    game.Name,
                    game.Version,
                    game.Description,
                    Tags = tagsJson,
                    Dependencies = depsJson,
                    ScreenshotUrls = screenshotsJson,
                    game.ManifestUrl,
                    game.SizeBytes,
                    LaunchConfig = launchConfigJson
                }, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task RemoveGamesNotInAsync(IReadOnlyCollection<string> keepIds)
    {
        if (keepIds.Count == 0) return;

        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        using var tx = conn.BeginTransaction();
        try
        {
            var idList = string.Join(",", keepIds.Select((_, i) => $"@p{i}"));
            var parameters = new DynamicParameters();
            var i = 0;
            foreach (var id in keepIds)
            {
                parameters.Add($"p{i++}", id);
            }

            await conn.ExecuteAsync($"DELETE FROM game_local_state WHERE game_id NOT IN ({idList})", parameters, tx);
            await conn.ExecuteAsync($"DELETE FROM games WHERE id NOT IN ({idList})", parameters, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<Game?> GetGameAsync(string gameId)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();
        
        var row = await conn.QueryFirstOrDefaultAsync("""
            SELECT id, name, version, description, tags, dependencies, screenshot_urls, manifest_url, size_bytes, launch_config
            FROM games WHERE id = @Id
        """, new { Id = gameId });

        return row != null ? MapGame(row) : null;
    }

    public async Task<Game[]> GetAllGamesAsync()
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();
        
        var rows = await conn.QueryAsync("SELECT * FROM games");
        return rows.Select(MapGame).ToArray();
    }

    public async Task<Game[]> GetGamesByStatusAsync(InstallStatus status)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();
        
        var rows = await conn.QueryAsync("""
            SELECT g.* FROM games g
            JOIN game_local_state s ON g.id = s.game_id
            WHERE s.status = @Status
        """, new { Status = status.ToString() });
        
        return rows.Select(MapGame).ToArray();
    }

    private static Game MapGame(dynamic row)
    {
        var tagsJson = row.tags as string;
        var depsJson = row.dependencies as string;
        var screenshotsJson = row.screenshot_urls as string;
        var launchConfigJson = row.launch_config as string;
        
        var tags = tagsJson != null ? JsonSerializer.Deserialize<string[]>(tagsJson) ?? [] : [];
        var deps = depsJson != null ? JsonSerializer.Deserialize<string[]>(depsJson) ?? [] : [];
        var screenshots = screenshotsJson != null ? JsonSerializer.Deserialize<string[]>(screenshotsJson) ?? [] : [];
        var launchConfig = launchConfigJson != null ? JsonSerializer.Deserialize<LaunchConfig>(launchConfigJson) : null;

        return new Game(
            row.id, row.name, row.version, row.description,
            tags, deps, screenshots, row.manifest_url,
            (long)row.size_bytes, launchConfig
        );
    }

    public async Task UpsertLocalStateAsync(GameLocalState state)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        long? lastPlayed = state.LastPlayed.HasValue 
            ? new DateTimeOffset(state.LastPlayed.Value).ToUnixTimeSeconds() 
            : null;
        var installedManifestJson = state.InstalledManifest != null
            ? JsonSerializer.Serialize(state.InstalledManifest)
            : null;
        await conn.ExecuteAsync("""
            INSERT INTO game_local_state (game_id, status, installed_path, play_time_seconds, last_played, installed_version, installed_manifest)
            VALUES (@GameId, @Status, @InstalledPath, @PlayTimeSeconds, @LastPlayed, @InstalledVersion, @InstalledManifest)
            ON CONFLICT(game_id) DO UPDATE SET
                status=@Status, installed_path=@InstalledPath, play_time_seconds=@PlayTimeSeconds,
                last_played=@LastPlayed, installed_version=@InstalledVersion, installed_manifest=@InstalledManifest
        """, new
        {
            state.GameId,
            Status = state.Status.ToString(),
            state.InstalledPath,
            state.PlayTimeSeconds,
            LastPlayed = lastPlayed,
            state.InstalledVersion,
            InstalledManifest = installedManifestJson
        });
    }

    public async Task<GameLocalState?> GetLocalStateAsync(string gameId)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        var row = await conn.QueryFirstOrDefaultAsync("""
            SELECT game_id, status, installed_path, play_time_seconds, last_played, installed_version, installed_manifest
            FROM game_local_state WHERE game_id = @GameId
        """, new { GameId = gameId });

        return row != null ? MapLocalState(row) : null;
    }

    public async Task<IReadOnlyList<GameLocalState>> GetAllLocalStatesAsync()
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        var rows = await conn.QueryAsync("SELECT * FROM game_local_state");
        return rows.Select(MapLocalState).ToArray();
    }

    private static GameLocalState MapLocalState(dynamic row)
    {
        var lastPlayedUnix = LongOrZero(row, "last_played");
        DateTime? lastPlayed = lastPlayedUnix != 0 
            ? DateTimeOffset.FromUnixTimeSeconds(lastPlayedUnix).DateTime 
            : null;

        var manifestJson = row.installed_manifest as string;
        var manifest = manifestJson != null
            ? JsonSerializer.Deserialize<GameManifest>(manifestJson)
            : null;

        return new GameLocalState(
            row.game_id,
            Enum.Parse<InstallStatus>(row.status as string ?? "NotInstalled"),
            row.installed_path as string,
            (long)row.play_time_seconds,
            lastPlayed,
            row.installed_version as string,
            manifest
        );
    }

    public async Task UpsertDownloadTaskAsync(DownloadTask task)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        long? startedAt = task.StartedAt.HasValue ? new DateTimeOffset(task.StartedAt.Value).ToUnixTimeSeconds() : (long?)null;
        long? completedAt = task.CompletedAt.HasValue ? new DateTimeOffset(task.CompletedAt.Value).ToUnixTimeSeconds() : (long?)null;
        
        await conn.ExecuteAsync("""
            INSERT INTO downloads (id, game_id, remote_url, local_path, total_bytes, downloaded_bytes, status, error, started_at, completed_at)
            VALUES (@Id, @GameId, @RemoteUrl, @LocalPath, @TotalBytes, @DownloadedBytes, @Status, @Error, @StartedAt, @CompletedAt)
            ON CONFLICT(id) DO UPDATE SET
                total_bytes=@TotalBytes, downloaded_bytes=@DownloadedBytes, status=@Status, error=@Error, completed_at=@CompletedAt
        """, new
        {
            task.Id,
            task.GameId,
            task.RemoteUrl,
            task.LocalPath,
            task.TotalBytes,
            task.DownloadedBytes,
            Status = task.Status.ToString(),
            task.Error,
            StartedAt = startedAt,
            CompletedAt = completedAt
        });
    }

    public async Task<DownloadTask?> GetDownloadTaskAsync(string taskId)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        var row = await conn.QueryFirstOrDefaultAsync("SELECT * FROM downloads WHERE id = @Id", new { Id = taskId });
        return row != null ? MapDownloadTask(row) : null;
    }

    public async Task<IReadOnlyList<DownloadTask>> GetAllDownloadTasksAsync()
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        var rows = await conn.QueryAsync("SELECT * FROM downloads ORDER BY started_at DESC");
        return rows.Select(MapDownloadTask).ToArray();
    }

    public async Task DeleteDownloadTaskAsync(string taskId)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM downloads WHERE id = @Id", new { Id = taskId });
    }

    private static DownloadTask MapDownloadTask(dynamic row)
    {
        var startedAtUnix = LongOrZero(row, "started_at");
        var completedAtUnix = LongOrZero(row, "completed_at");
        
        DateTime? startedAt = startedAtUnix != 0 
            ? DateTimeOffset.FromUnixTimeSeconds(startedAtUnix).DateTime 
            : null;
        DateTime? completedAt = completedAtUnix != 0 
            ? DateTimeOffset.FromUnixTimeSeconds(completedAtUnix).DateTime 
            : null;
        
        return new DownloadTask(
            row.id, row.game_id, row.remote_url, row.local_path,
            (long)row.total_bytes, (long)row.downloaded_bytes,
            Enum.Parse<DownloadStatus>(row.status as string ?? "Queued"),
            row.error as string,
            startedAt,
            completedAt
        );
    }

    private static long LongOrZero(dynamic row, string columnName)
    {
        var value = ((System.Collections.Generic.IDictionary<string, object>)row)[columnName];
        return value == null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>
    /// Settings are read for every install and every cover, so they are kept in memory.
    /// A copy is returned because callers mutate AppSettings (e.g. to store a detected RootFolder).
    /// </summary>
    public async Task<AppSettings> GetSettingsAsync()
    {
        var cached = _settingsCache;
        if (cached != null) return cached.Clone();

        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        var row = await conn.QueryFirstOrDefaultAsync("SELECT value FROM settings WHERE key = 'app_settings'");
        var value = row?.value as string;
        var settings = value != null
            ? JsonSerializer.Deserialize<AppSettings>(value) ?? new AppSettings()
            : new AppSettings();

        _settingsCache = settings;
        return settings.Clone();
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await InitializeAsync();
        await using var conn = await OpenConnectionAsync();

        var json = JsonSerializer.Serialize(settings);
        await conn.ExecuteAsync("""
            INSERT INTO settings (key, value) VALUES ('app_settings', @Value)
            ON CONFLICT(key) DO UPDATE SET value=@Value
        """, new { Value = json });

        _settingsCache = settings.Clone();
    }
}