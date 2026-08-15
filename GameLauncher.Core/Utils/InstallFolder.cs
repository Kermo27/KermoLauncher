namespace GameLauncher.Core.Utils;

/// <summary>Default path and writability checks for the game install directory.</summary>
public static class InstallFolder
{
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

            freeBytes = new DriveInfo(Path.GetPathRoot(full) ?? full).AvailableFreeSpace;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
