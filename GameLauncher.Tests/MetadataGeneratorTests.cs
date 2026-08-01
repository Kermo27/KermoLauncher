namespace GameLauncher.Tests;

using GameLauncher.AdminTool.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Xunit;

public class MetadataGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gl-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _errors = [];
    private readonly MetadataGenerator _generator;

    public MetadataGeneratorTests()
    {
        Directory.CreateDirectory(_root);
        _generator = new MetadataGenerator(new CapturingLogger(_errors));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task ScanFolderAsync_CollectsFilesAndSizes()
    {
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "my-game")).FullName;
        Directory.CreateDirectory(Path.Combine(gameDir, "data"));
        await File.WriteAllTextAsync(Path.Combine(gameDir, "game.exe"), new string('a', 500));
        await File.WriteAllTextAsync(Path.Combine(gameDir, "data", "level.dat"), new string('b', 250));

        var games = await _generator.ScanFolderAsync(_root);

        var game = Assert.Single(games);
        Assert.Equal("my-game", game.Id);
        Assert.Equal("My Game", game.Name);
        Assert.Equal(750, game.SizeBytes);
        Assert.Equal(2, game.Files.Length);
        Assert.Equal("my-game/manifest.json", game.ManifestUrl);
        Assert.Equal(gameDir, game.LocalFolder);
    }

    [Fact]
    public async Task ScanFolderAsync_ExcludesManifestAndScreenshots()
    {
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "game-a")).FullName;
        await File.WriteAllTextAsync(Path.Combine(gameDir, "game.exe"), new string('c', 100));
        Directory.CreateDirectory(Path.Combine(gameDir, "screenshots"));
        await File.WriteAllTextAsync(Path.Combine(gameDir, "screenshots", "shot.png"), new string('d', 10));
        await File.WriteAllTextAsync(Path.Combine(gameDir, "manifest.json"), "{}");

        var games = await _generator.ScanFolderAsync(_root);

        var game = Assert.Single(games);
        Assert.Single(game.Files);
        Assert.Equal("game.exe", game.Files[0].Path);
        Assert.Contains("screenshots/shot.png", game.ScreenshotPaths);
    }

    [Fact]
    public async Task ScanFolderAsync_EmptyFolder_ReturnsNoGames()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-dir"));

        var games = await _generator.ScanFolderAsync(_root);

        Assert.Empty(games);
    }

    [Fact]
    public async Task GenerateMetadataJsonAsync_WritesManifestAndMetadata()
    {
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "game-b")).FullName;
        await File.WriteAllTextAsync(Path.Combine(gameDir, "game.exe"), "x");
        var games = await _generator.ScanFolderAsync(_root);

        var metadataPath = Path.Combine(_root, "metadata.json");
        await _generator.GenerateMetadataJsonAsync(games, metadataPath);

        Assert.True(File.Exists(Path.Combine(gameDir, "manifest.json")));
        Assert.True(File.Exists(metadataPath));

        var json = await File.ReadAllTextAsync(metadataPath);
        Assert.Contains("manifestUrl", json);
        Assert.Contains("Game B", json);
        Assert.Contains("game-b/manifest.json", json);

        var manifestJson = await File.ReadAllTextAsync(Path.Combine(gameDir, "manifest.json"));
        Assert.Contains("sha256", manifestJson);
        Assert.Contains("game.exe", manifestJson);
        Assert.Contains("\"version\": \"1.0.0\"", manifestJson);
    }

    [Fact]
    public async Task ScanFolderAsync_AlwaysComputesFreshHashes()
    {
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "game-c")).FullName;
        var exePath = Path.Combine(gameDir, "game.exe");
        await File.WriteAllTextAsync(exePath, new string('e', 100));
        await File.WriteAllTextAsync(Path.Combine(gameDir, "level.dat"), new string('f', 50));

        var first = (await _generator.ScanFolderAsync(_root)).Single();

        // Change content but keep the same size
        await File.WriteAllTextAsync(exePath, new string('g', 100));

        var second = (await _generator.ScanFolderAsync(_root)).Single();
        var unchanged = second.Files.Single(f => f.Path == "level.dat");
        var changed = second.Files.Single(f => f.Path == "game.exe");
        var firstUnchanged = first.Files.Single(f => f.Path == "level.dat");

        // Unchanged file gets the same hash (same content)
        Assert.Equal(firstUnchanged.Sha256, unchanged.Sha256);
        // Same size but different content must get a NEW hash
        Assert.NotEqual(firstUnchanged.Sha256, changed.Sha256);
        var firstChanged = first.Files.Single(f => f.Path == "game.exe");
        Assert.NotEqual(firstChanged.Sha256, changed.Sha256);
    }

    [Fact]
    public async Task ComputeSha256Async_MatchesKnownHash()
    {
        var path = Path.Combine(_root, "hashme.bin");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("hello"));

        var sha = await MetadataGenerator.ComputeSha256Async(path);

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", sha);
    }

    private sealed class CapturingLogger : ILogger<MetadataGenerator>
    {
        private readonly List<string> _errors;
        public CapturingLogger(List<string> errors) => _errors = errors;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception != null) _errors.Add(exception.ToString());
        }
    }
}
