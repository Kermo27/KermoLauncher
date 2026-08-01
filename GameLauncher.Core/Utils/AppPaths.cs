namespace GameLauncher.Core.Utils;

public static class AppPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var newDir = Path.Combine(baseDir, "KermoLauncher");
        var oldDir = Path.Combine(baseDir, "GameLauncher");

        try
        {
            Directory.CreateDirectory(newDir);
            if (Directory.Exists(oldDir) && !Directory.EnumerateFileSystemEntries(newDir).Any())
            {
                foreach (var file in Directory.GetFiles(oldDir))
                {
                    File.Move(file, Path.Combine(newDir, Path.GetFileName(file)));
                }
                foreach (var dir in Directory.GetDirectories(oldDir))
                {
                    Directory.Move(dir, Path.Combine(newDir, Path.GetFileName(dir)));
                }
                if (!Directory.EnumerateFileSystemEntries(oldDir).Any())
                {
                    Directory.Delete(oldDir);
                }
            }
        }
        catch
        {
            // Migration is best-effort; fall back to the new directory anyway
        }

        return newDir;
    }
}
