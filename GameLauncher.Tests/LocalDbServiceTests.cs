namespace GameLauncher.Tests;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using Xunit;

public class LocalDbServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "gl-db-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly LocalDbService _db;

    public LocalDbServiceTests()
    {
        _db = new LocalDbService(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private static Game Game(string id) => new Game(
        Id: id,
        Name: id,
        Version: "1.0.0",
        Description: "",
        Tags: [],
        Dependencies: [],
        ScreenshotUrls: [],
        ManifestUrl: $"{id}/manifest.json",
        SizeBytes: 0,
        LaunchConfig: null);

    [Fact]
    public async Task RemoveGamesNotInAsync_DeletesMissingGamesAndStates()
    {
        await _db.UpsertGamesAsync([Game("a"), Game("b"), Game("c")]);
        await _db.UpsertLocalStateAsync(new GameLocalState("b", InstallStatus.Installed));
        await _db.UpsertLocalStateAsync(new GameLocalState("c", InstallStatus.Failed));

        await _db.RemoveGamesNotInAsync(["a", "b"]);

        var remaining = (await _db.GetAllGamesAsync()).Select(g => g.Id).OrderBy(x => x).ToArray();
        Assert.Equal(["a", "b"], remaining);
        Assert.NotNull(await _db.GetLocalStateAsync("b"));
        Assert.Null(await _db.GetLocalStateAsync("c"));
    }

    [Fact]
    public async Task RemoveGamesNotInAsync_KeepsEverythingWhenAllPresent()
    {
        await _db.UpsertGamesAsync([Game("a"), Game("b")]);

        await _db.RemoveGamesNotInAsync(["a", "b"]);

        Assert.Equal(2, (await _db.GetAllGamesAsync()).Length);
    }

    [Fact]
    public async Task RemoveGamesNotInAsync_EmptyListIsNoOp()
    {
        await _db.UpsertGamesAsync([Game("a")]);

        await _db.RemoveGamesNotInAsync([]);

        Assert.Single(await _db.GetAllGamesAsync());
    }
}
