using GameLauncher.AdminTool.ViewModels;
using GameLauncher.Core.Models;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLauncher.AdminTool.Services;

public class MetadataGenerator
{
    private readonly ILogger<MetadataGenerator> _logger;

    public MetadataGenerator(ILogger<MetadataGenerator> logger)
    {
        _logger = logger;
    }

    public async Task<GameMetadata[]> ScanFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        var games = new List<GameMetadata>();
        var gameDirs = Directory.GetDirectories(folderPath);

        foreach (var dir in gameDirs)
        {
            try
            {
                var game = await ScanGameDirectoryAsync(dir);
                if (game != null)
                {
                    games.Add(game);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan game directory: {Dir}", dir);
            }
        }

        return games.ToArray();
    }

    private async Task<GameMetadata?> ScanGameDirectoryAsync(string dir)
    {
        var dirName = Path.GetFileName(dir);

        // Collect game files: everything except screenshots/ and manifest.json
        var screenshotPaths = CollectScreenshots(dir);
        var gameFiles = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(dir, f, screenshotPaths))
            .Select(f => Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p)
            .ToArray();

        if (gameFiles.Length == 0)
        {
            _logger.LogWarning("No game files found in {Dir}", dir);
            return null;
        }

        // Reuse the version from an existing manifest if present
        var existingManifest = TryLoadManifest(Path.Combine(dir, "manifest.json"));

        var files = new GameFile[gameFiles.Length];
        var totalBytes = 0L;
        for (var i = 0; i < gameFiles.Length; i++)
        {
            var absPath = Path.Combine(dir, gameFiles[i]);
            var size = new FileInfo(absPath).Length;
            var sha = await ComputeSha256Async(absPath);
            files[i] = new GameFile(gameFiles[i], size, sha);
            totalBytes += size;
        }

        var id = dirName.ToLowerInvariant().Replace(' ', '-');

        return new GameMetadata
        {
            Id = id,
            Name = FormatGameName(dirName),
            Version = existingManifest?.Version ?? "1.0.0",
            Description = "",
            Tags = [],
            Dependencies = [],
            ScreenshotPaths = screenshotPaths,
            LocalFolder = dir,
            RemoteFolder = dirName,
            ManifestUrl = $"{dirName}/manifest.json",
            Files = files,
            SizeBytes = totalBytes
        };
    }

    private static bool IsExcluded(string gameDir, string filePath, string[] screenshotPaths)
    {
        var relative = Path.GetRelativePath(gameDir, filePath);
        if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (relative.StartsWith("screenshots" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        if (relative.StartsWith("screenshots/", StringComparison.OrdinalIgnoreCase)) return true;
        return screenshotPaths.Contains(relative);
    }

    private static string[] CollectScreenshots(string dir)
    {
        var screenshotsDir = Path.Combine(dir, "screenshots");
        return Directory.Exists(screenshotsDir)
            ? Directory.GetFiles(screenshotsDir, "*.jpg")
                .Concat(Directory.GetFiles(screenshotsDir, "*.png"))
                .Concat(Directory.GetFiles(screenshotsDir, "*.jpeg"))
                .Select(f => Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray()
            : [];
    }

    private static GameManifest? TryLoadManifest(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<GameManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files is null)
            {
                return null;
            }
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads dest/manifest.json when present; otherwise hashes the folder so a copy made
    /// without AdminTool still diffs correctly.
    /// </summary>
    public async Task<GameManifest?> ReadFolderManifestAsync(string gameDir)
    {
        var fromFile = TryLoadManifest(Path.Combine(gameDir, "manifest.json"));
        if (fromFile != null) return fromFile;
        if (!Directory.Exists(gameDir)) return null;

        var scanned = await ScanGameDirectoryAsync(gameDir);
        return scanned == null ? null : new GameManifest(scanned.Version, scanned.SizeBytes, scanned.Files);
    }

    /// <summary>
    /// Writes dest/metadata.json, keeping catalog entries for games this run did not touch.
    /// </summary>
    public async Task UpsertCatalogAsync(string destRoot, GameMetadata[] published)
    {
        var path = Path.Combine(destRoot, "metadata.json");
        var existing = TryLoadCatalog(path);
        var byId = existing.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var game in published)
        {
            byId[game.Id] = ToCatalogEntry(game);
        }

        var json = JsonSerializer.Serialize(byId.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        await File.WriteAllTextAsync(path, json);
        _logger.LogInformation("Updated catalog at {Path} with {Count} games", path, byId.Count);
    }

    private static Game[] TryLoadCatalog(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var games = JsonSerializer.Deserialize<Game[]>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return games ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Game ToCatalogEntry(GameMetadata g) => new(
        g.Id,
        g.Name,
        g.Version,
        g.Description,
        g.Tags,
        g.Dependencies,
        g.ScreenshotPaths.Select(p => $"{g.RemoteFolder}/screenshots/{Path.GetFileName(p)}").ToArray(),
        g.ManifestUrl,
        g.SizeBytes,
        g.LaunchConfig);

    internal static string FormatGameName(string dirName)
    {
        var parts = dirName.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return dirName;
        return string.Join(' ', parts.Select(w =>
            char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..] : "")));
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            using var sha256 = SHA256.Create();
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var hash = await sha256.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }, ct);
    }

    public async Task GenerateMetadataJsonAsync(GameMetadata[] games, string outputPath)
    {
        // 1. Write manifest.json into each game folder
        foreach (var game in games)
        {
            if (string.IsNullOrWhiteSpace(game.LocalFolder) || !Directory.Exists(game.LocalFolder)) continue;

            var manifest = new GameManifest(game.Version, game.SizeBytes, game.Files);
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(Path.Combine(game.LocalFolder, "manifest.json"), manifestJson);
        }

        // 2. Build metadata.json
        var gameModels = games.Select(g => new Game(
            g.Id,
            g.Name,
            g.Version,
            g.Description,
            g.Tags,
            g.Dependencies,
            g.ScreenshotPaths.Select(p => $"{g.RemoteFolder}/screenshots/{Path.GetFileName(p)}").ToArray(),
            g.ManifestUrl,
            g.SizeBytes,
            g.LaunchConfig
        )).ToArray();

        var json = JsonSerializer.Serialize(gameModels, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await File.WriteAllTextAsync(outputPath, json);
        _logger.LogInformation("Generated metadata.json at {Path} with {Count} games", outputPath, games.Length);
    }
}
