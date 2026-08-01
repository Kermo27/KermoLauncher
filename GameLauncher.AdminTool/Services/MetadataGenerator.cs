using GameLauncher.AdminTool.ViewModels;
using GameLauncher.Core.Models;
using GameLauncher.Core.Utils;
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
        
        // Look for zip file
        var zipFiles = Directory.GetFiles(dir, "*.zip", SearchOption.TopDirectoryOnly);
        var zipPath = zipFiles.FirstOrDefault();
        
        if (zipPath == null)
        {
            _logger.LogWarning("No zip file found in {Dir}", dir);
            return null;
        }

        // Look for screenshots
        var screenshotsDir = Path.Combine(dir, "screenshots");
        var screenshotPaths = Directory.Exists(screenshotsDir) 
            ? Directory.GetFiles(screenshotsDir, "*.jpg")
                .Concat(Directory.GetFiles(screenshotsDir, "*.png"))
                .Concat(Directory.GetFiles(screenshotsDir, "*.jpeg"))
                .Select(f => Path.GetRelativePath(dir, f))
                .ToArray()
            : [];

        // Compute SHA256 and size
        var fileInfo = new FileInfo(zipPath);
        var sha256 = await ComputeSha256Async(zipPath);

        // Try to parse version from zip filename
        var version = ExtractVersionFromFilename(Path.GetFileName(zipPath)) ?? "1.0.0";

        var id = dirName.ToLowerInvariant().Replace(' ', '-');

        return new GameMetadata
        {
            Id = id,
            Name = FormatGameName(dirName),
            Version = version,
            Description = "",
            Tags = [],
            Dependencies = [],
            ScreenshotPaths = screenshotPaths,
            LocalZipPath = zipPath,
            RemoteZipPath = $"{dirName}/{Path.GetFileName(zipPath)}",
            RemoteFolder = dirName,
            SizeBytes = fileInfo.Length,
            Sha256 = sha256
        };
    }

    private static string? ExtractVersionFromFilename(string filename)
    {
        // Try to extract version from patterns like: game-v1.0.0.zip, game_1.0.0.zip
        var patterns = new[]
        {
            @"v?(\d+\.\d+\.\d+)",
            @"v?(\d+\.\d+)",
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(filename, pattern);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        return null;
    }

    private static string FormatGameName(string dirName)
    {
        // Convert "game-name" or "game_name" to "Game Name"
        return string.Join(' ', dirName.Split('-', '_').Select(w => char.ToUpper(w[0]) + w[1..]));
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        return await Task.Run(() =>
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        });
    }

    public async Task GenerateMetadataJsonAsync(GameMetadata[] games, string outputPath)
    {
        var gameModels = games.Select(g => new Game(
            g.Id,
            g.Name,
            g.Version,
            g.Description,
            g.Tags,
            g.Dependencies,
            g.ScreenshotPaths.Select(p => $"{g.RemoteFolder}/screenshots/{Path.GetFileName(p)}").ToArray(),
            g.RemoteZipPath,
            g.SizeBytes,
            g.Sha256,
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