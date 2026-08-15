namespace GameLauncher.Core.Utils;

using GameLauncher.Core.Models;

/// <summary>Default path and writability checks for the game install directory.</summary>
public static class InstallFolder
{
    public const long DiskSpaceMarginBytes = 64L * 1024 * 1024;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "games");

    public static bool TryValidate(string path, out string? error, out long freeBytes)
    {
        error = null;
        freeBytes = 0;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "empty";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            Directory.CreateDirectory(full);

            var probe = Path.Combine(full, ".kermo-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            freeBytes = GetAvailableBytes(full) ?? 0;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Free space on the volume that holds <paramref name="path"/>, or null when it cannot be read.
    /// Creates the directory if it is missing so DriveInfo has something to resolve.
    /// </summary>
    public static long? GetAvailableBytes(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(full);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Throws when the volume has less than <paramref name="neededBytes"/>.
    /// Skips the check when free space cannot be read, so a flaky DriveInfo never blocks an install.
    /// </summary>
    public static void ThrowIfInsufficient(string path, long neededBytes)
    {
        if (neededBytes <= 0) return;
        var available = GetAvailableBytes(path);
        if (available is null) return;
        if (available.Value < neededBytes)
        {
            throw new InsufficientDiskSpaceException(neededBytes, available.Value);
        }
    }
}
