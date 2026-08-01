namespace GameLauncher.Core.Services;

using GameLauncher.Core.Models;

public static class ManifestDiff
{
    public static GameFile[] ComputeFilesToDownload(GameManifest remote, GameManifest? installed)
    {
        if (installed == null) return remote.Files;

        return remote.Files
            .Where(f => !installed.Files.Any(i =>
                string.Equals(i.Path, f.Path, StringComparison.OrdinalIgnoreCase) &&
                i.SizeBytes == f.SizeBytes &&
                string.Equals(i.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static GameFile[] ComputeStaleFiles(GameManifest remote, GameManifest installed)
    {
        var remotePaths = new HashSet<string>(remote.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        return installed.Files
            .Where(f => !remotePaths.Contains(f.Path))
            .ToArray();
    }

    public static bool IsSameFile(GameFile a, GameFile b)
    {
        return string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) &&
               a.SizeBytes == b.SizeBytes &&
               string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
