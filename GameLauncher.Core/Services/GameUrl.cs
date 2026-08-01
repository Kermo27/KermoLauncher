namespace GameLauncher.Core.Services;

using GameLauncher.Core.Models;

public static class GameUrl
{
    public static string GetFileUrl(NextcloudConfig config, string manifestUrl, string filePath)
    {
        var folder = Path.GetDirectoryName(manifestUrl.Replace('\\', '/'))?.TrimEnd('/') ?? "";
        var relative = folder.Length > 0 ? $"{folder}/{filePath}" : filePath;
        return config.GetFileUrl(relative);
    }
}
