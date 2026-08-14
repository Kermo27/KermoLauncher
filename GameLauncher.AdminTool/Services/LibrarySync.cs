using GameLauncher.Core.Models;
using GameLauncher.Core.Services;

namespace GameLauncher.AdminTool.Services;

public enum SyncChangeKind
{
    Added,
    Changed,
    Removed
}

public record SyncChange(string RelativePath, SyncChangeKind Kind, long SizeBytes);

public sealed class GameSyncPlan
{
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required string RemoteFolder { get; init; }
    public required string LocalFolder { get; init; }
    public required IReadOnlyList<SyncChange> Changes { get; init; }

    public IEnumerable<SyncChange> ToCopy =>
        Changes.Where(c => c.Kind is SyncChangeKind.Added or SyncChangeKind.Changed);

    public IEnumerable<SyncChange> ToDelete =>
        Changes.Where(c => c.Kind == SyncChangeKind.Removed);

    public bool IsUpToDate => Changes.Count == 0;

    public int AddedCount => Changes.Count(c => c.Kind == SyncChangeKind.Added);
    public int ChangedCount => Changes.Count(c => c.Kind == SyncChangeKind.Changed);
    public int RemovedCount => Changes.Count(c => c.Kind == SyncChangeKind.Removed);
    public long BytesToCopy => ToCopy.Sum(c => c.SizeBytes);
}

/// <summary>
/// Diffs a scanned (test) game against a copy already sitting in the Nextcloud sync folder.
/// SHA-256 comes from manifests, so a mod or patch shows up even when the file size is unchanged.
/// </summary>
public static class LibrarySync
{
    public static GameSyncPlan Plan(ViewModels.GameMetadata game, GameManifest? destManifest)
        => Plan(game.Id, game.Name, game.RemoteFolder, game.LocalFolder, game.Files, destManifest);

    public static GameSyncPlan Plan(
        string gameId,
        string gameName,
        string remoteFolder,
        string localFolder,
        GameFile[] localFiles,
        GameManifest? destManifest)
    {
        var destByPath = (destManifest?.Files ?? [])
            .ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        var changes = new List<SyncChange>();
        var localPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var local in localFiles)
        {
            localPaths.Add(local.Path);
            if (!destByPath.TryGetValue(local.Path, out var dest))
            {
                changes.Add(new SyncChange(local.Path, SyncChangeKind.Added, local.SizeBytes));
                continue;
            }

            if (!ManifestDiff.IsSameFile(local, dest))
                changes.Add(new SyncChange(local.Path, SyncChangeKind.Changed, local.SizeBytes));
        }

        if (destManifest != null)
        {
            foreach (var dest in destManifest.Files)
            {
                if (!localPaths.Contains(dest.Path))
                    changes.Add(new SyncChange(dest.Path, SyncChangeKind.Removed, dest.SizeBytes));
            }
        }

        return new GameSyncPlan
        {
            GameId = gameId,
            GameName = gameName,
            RemoteFolder = remoteFolder,
            LocalFolder = localFolder,
            Changes = changes
        };
    }
}
