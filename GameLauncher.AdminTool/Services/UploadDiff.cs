using GameLauncher.Core.Models;

namespace GameLauncher.AdminTool.Services;

public static class UploadDiff
{
    public static GameFile[] FilesToUpload(GameFile[] localFiles, GameManifest? remoteManifest)
    {
        var plan = LibrarySync.Plan("", "", "", "", localFiles, remoteManifest);
        var copy = new HashSet<string>(
            plan.ToCopy.Select(c => c.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        return localFiles.Where(f => copy.Contains(f.Path)).ToArray();
    }
}
