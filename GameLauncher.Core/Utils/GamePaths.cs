namespace GameLauncher.Core.Utils;

/// <summary>
/// Joins install-relative paths from manifests and launch configs. Manifests often use
/// Windows separators; Path.Combine would treat "bin\game.exe" as a single file name on Unix.
/// </summary>
public static class GamePaths
{
    public static string Combine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return root;

        var parts = relative
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length == 0 ? root : Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    public static bool LooksLikeWindowsBinary(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);
    }

    public static void TryMakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        if (LooksLikeWindowsBinary(path)) return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
