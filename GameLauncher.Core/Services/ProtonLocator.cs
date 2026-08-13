namespace GameLauncher.Core.Services;

/// <summary>
/// Discovers Steam/GE-Proton installs and Steam Linux Runtime, similar to SOFL.
/// </summary>
public static class ProtonLocator
{
    public sealed record ProtonInstall(string Name, string Directory, string ProtonScript);

    /// <summary>Installed Proton builds, newest name first.</summary>
    public static IReadOnlyList<ProtonInstall> FindInstalled(string? home = null)
    {
        home ??= DefaultHome();
        var found = new Dictionary<string, ProtonInstall>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in CompatToolRoots(home))
            CollectFromDirectory(dir, found);

        foreach (var dir in SteamCommonRoots(home))
            CollectFromDirectory(dir, found, namePrefix: "Proton");

        // Prefer user GE-Proton under ~/.local over /usr packages: pressure-vessel cannot
        // share /usr into the Steam Runtime container (breaks overlays / DLL loads).
        return found.Values
            .OrderByDescending(Rank)
            .ThenByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ProtonInstall? Resolve(string? preferredVersion, string? home = null)
    {
        var installed = FindInstalled(home);
        if (installed.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredVersion))
        {
            var match = installed.FirstOrDefault(p =>
                string.Equals(p.Name, preferredVersion.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return installed[0];
    }

    public static int Rank(ProtonInstall install)
    {
        var score = 0;
        var dir = install.Directory;
        var name = install.Name;

        if (!dir.StartsWith("/usr/", StringComparison.Ordinal))
            score += 1000;

        if (name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase))
            score += 100;
        else if (name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase))
            score += 50;

        if (name.Contains("slr", StringComparison.OrdinalIgnoreCase))
            score -= 200;

        return score;
    }

    /// <summary>Path to umu-run when present on PATH.</summary>
    public static string? FindUmuRun()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "umu-run");
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var candidate in new[] { "/usr/bin/umu-run", "/usr/local/bin/umu-run" })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static string? FindSteamClientRoot(string? home = null)
    {
        home ??= DefaultHome();
        foreach (var candidate in new[]
                 {
                     Path.Combine(home, ".local", "share", "Steam"),
                     Path.Combine(home, ".steam", "steam"),
                     Path.Combine(home, ".steam", "root")
                 })
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>SteamLinuxRuntime_sniper/run when installed (Steam app 1628350).</summary>
    private static string DefaultHome() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string? FindSteamRuntime(string? home = null)
    {
        home ??= DefaultHome();
        var steamRoot = FindSteamClientRoot(home);
        if (steamRoot == null) return null;

        foreach (var libraryRoot in EnumerateSteamLibraryRoots(steamRoot, home))
        {
            var runtime = Path.Combine(libraryRoot, "steamapps", "common", "SteamLinuxRuntime_sniper", "run");
            if (File.Exists(runtime)) return runtime;
        }

        var legacy = Path.Combine(steamRoot, "ubuntu12_32", "steam-runtime", "run.sh");
        return File.Exists(legacy) ? legacy : null;
    }

    private static IEnumerable<string> CompatToolRoots(string home)
    {
        yield return Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d");
        yield return Path.Combine(home, ".steam", "root", "compatibilitytools.d");
        yield return Path.Combine(home, ".steam", "steam", "compatibilitytools.d");
        yield return "/usr/share/steam/compatibilitytools.d";
    }

    private static IEnumerable<string> SteamCommonRoots(string home)
    {
        yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common");
        yield return Path.Combine(home, ".steam", "steam", "steamapps", "common");
        yield return Path.Combine(home, ".steam", "root", "steamapps", "common");
    }

    private static IEnumerable<string> EnumerateSteamLibraryRoots(string steamRoot, string home)
    {
        yield return steamRoot;

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            vdf = Path.Combine(home, ".steam", "steam", "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        // libraryfolders.vdf is small; a light parse avoids a VDF dependency.
        foreach (var line in File.ReadLines(vdf))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split('"');
            if (parts.Length < 4) continue;
            var path = parts[3].Replace("\\\\", "\\");
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static void CollectFromDirectory(
        string directory,
        Dictionary<string, ProtonInstall> into,
        string? namePrefix = null)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(dir);
            if (namePrefix != null)
            {
                if (!name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            }
            else if (!(name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
                       || name.Contains("proton", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var script = Path.Combine(dir, "proton");
            if (!File.Exists(script)) continue;
            into[name] = new ProtonInstall(name, dir, script);
        }
    }
}
