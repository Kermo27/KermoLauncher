using GameLauncher.AdminTool.ViewModels;
using GameLauncher.Core.Models;
using System.Text.Json;

namespace GameLauncher.AdminTool.Services;

/// <summary>
/// Copies only the delta from a test game folder into the Nextcloud-synced Games directory.
/// Nextcloud's desktop client then uploads those files; no WebDAV login is involved.
/// </summary>
public sealed class LibraryPublisher
{
    private readonly MetadataGenerator _metadata;

    public LibraryPublisher(MetadataGenerator metadata)
    {
        _metadata = metadata;
    }

    public async Task<GameSyncPlan> CompareAsync(GameMetadata game, string destRoot, CancellationToken ct = default)
    {
        var destGameDir = Path.Combine(destRoot, game.RemoteFolder);
        var destManifest = await _metadata.ReadFolderManifestAsync(destGameDir);
        ct.ThrowIfCancellationRequested();
        return LibrarySync.Plan(game, destManifest);
    }

    public async Task PublishAsync(
        string destRoot,
        GameMetadata game,
        GameSyncPlan plan,
        IProgress<PublishProgress>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game.LocalFolder) || !Directory.Exists(game.LocalFolder))
            throw new DirectoryNotFoundException(game.LocalFolder);

        var destGameDir = Path.Combine(destRoot, game.RemoteFolder);
        Directory.CreateDirectory(destGameDir);

        var copy = plan.ToCopy.ToArray();
        var delete = plan.ToDelete.ToArray();
        var extra = ScreenshotCopies(game, destGameDir);
        var total = copy.Length + extra.Length + delete.Length + 1;
        var done = 0;

        foreach (var change in copy)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(game.LocalFolder, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var dest = Path.Combine(destGameDir, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            done++;
            progress?.Report(new PublishProgress(change.RelativePath, done, total));
        }

        foreach (var shot in extra)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(game.LocalFolder, shot.Replace('/', Path.DirectorySeparatorChar));
            var dest = Path.Combine(destGameDir, shot.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            done++;
            progress?.Report(new PublishProgress(shot, done, total));
        }

        foreach (var change in delete)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(destGameDir, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest)) File.Delete(dest);
            done++;
            progress?.Report(new PublishProgress(change.RelativePath, done, total));
        }

        var manifest = new GameManifest(game.Version, game.SizeBytes, game.Files);
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(Path.Combine(destGameDir, "manifest.json"), manifestJson, ct);
        done++;
        progress?.Report(new PublishProgress("manifest.json", done, total));
    }

    private static string[] ScreenshotCopies(GameMetadata game, string destGameDir)
    {
        var needed = new List<string>();
        foreach (var shot in game.ScreenshotPaths)
        {
            var src = Path.Combine(game.LocalFolder, shot.Replace('/', Path.DirectorySeparatorChar));
            var dest = Path.Combine(destGameDir, shot.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            if (!File.Exists(dest) || new FileInfo(src).Length != new FileInfo(dest).Length)
                needed.Add(shot);
        }
        return needed.ToArray();
    }
}

public record PublishProgress(string RelativePath, int Completed, int Total);
