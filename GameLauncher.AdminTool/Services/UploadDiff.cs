using GameLauncher.Core.Models;
using GameLauncher.Core.Services;

namespace GameLauncher.AdminTool.Services;

public static class UploadDiff
{
    public static GameFile[] FilesToUpload(GameFile[] localFiles, GameManifest? remoteManifest)
    {
        if (remoteManifest == null) return localFiles;

        return localFiles
            .Where(f => !remoteManifest.Files.Any(r => ManifestDiff.IsSameFile(f, r)))
            .ToArray();
    }
}
