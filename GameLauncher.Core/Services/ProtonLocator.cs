namespace GameLauncher.Core.Services;

/// <summary>
/// Discovers Steam/GE-Proton installs and Steam Linux Runtime, similar to SOFL.
/// </summary>
public static class ProtonLocator
{
    public sealed record ProtonInstall(string Name, string Directory, string ProtonScript)
    {
        /// <summary>
        /// Steam appid of the container runtime this build declares in toolmanifest.vdf
        /// ("require_tool_appid"). Null when the build runs without one.
        /// </summary>
        public string? RequiredRuntimeAppId { get; init; }
    }

    /// <summary>
    /// Runtime folder names by the appid Proton asks for. The pairing matters: GE-Proton 11 wants
    /// runtime 4.0 (Python 3.13) and dies on an import when started under sniper (Python 3.9).
    /// </summary>
    private static readonly Dictionary<string, string> RuntimeDirByAppId = new(StringComparer.Ordinal)
    {
        ["1070560"] = "SteamLinuxRuntime",
        ["1391110"] = "SteamLinuxRuntime_soldier",
        ["1628350"] = "SteamLinuxRuntime_sniper",
        ["4183110"] = "SteamLinuxRuntime_4"
    };

    /// <summary>umu keeps its own copies of the same runtimes; they work just as well.</summary>
    private static readonly Dictionary<string, string> UmuRuntimeDirByAppId = new(StringComparer.Ordinal)
    {
        ["1628350"] = "steamrt3",
        ["4183110"] = "steamrt4"
    };

    private static readonly Dictionary<string, string> RuntimeLabelByAppId = new(StringComparer.Ordinal)
    {
        ["1070560"] = "Steam Linux Runtime 1.0 (scout)",
        ["1391110"] = "Steam Linux Runtime 2.0 (soldier)",
        ["1628350"] = "Steam Linux Runtime 3.0 (sniper)",
        ["4183110"] = "Steam Linux Runtime 4.0"
    };

    /// <summary>Human-readable runtime name for error messages.</summary>
    public static string DescribeRuntime(string appId) =>
        RuntimeLabelByAppId.TryGetValue(appId, out var label)
            ? $"{label} (appid {appId})"
            : $"Steam Linux Runtime with appid {appId}";

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

        // Newest is not automatically usable: a build whose required runtime is missing cannot
        // start at all, so one that has its runtime installed wins. The list is already ordered by
        // rank, and OrderBy is stable, so that order decides among equally usable builds.
        return installed
            .OrderByDescending(p => HasRequiredRuntime(p, home) ? 1 : 0)
            .First();
    }

    /// <summary>True when the build needs no container runtime or the one it needs is installed.</summary>
    public static bool HasRequiredRuntime(ProtonInstall install, string? home = null) =>
        install.RequiredRuntimeAppId is not { Length: > 0 } || FindSteamRuntime(install, home) != null;

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

    private static string DefaultHome() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Entry point of the container runtime this Proton build asks for, or null when it declares
    /// none or that runtime is not installed anywhere.
    /// </summary>
    public static string? FindSteamRuntime(ProtonInstall install, string? home = null) =>
        install.RequiredRuntimeAppId is { Length: > 0 } appId
            ? FindRuntimeForAppId(appId, home)
            : null;

    public static string? FindRuntimeForAppId(string appId, string? home = null)
    {
        home ??= DefaultHome();
        if (!RuntimeDirByAppId.TryGetValue(appId, out var dirName)) return null;

        var steamRoot = FindSteamClientRoot(home);
        if (steamRoot != null)
        {
            foreach (var libraryRoot in EnumerateSteamLibraryRoots(steamRoot, home))
            {
                var entry = RuntimeEntryPoint(Path.Combine(libraryRoot, "steamapps", "common", dirName));
                if (entry != null) return entry;
            }
        }

        if (UmuRuntimeDirByAppId.TryGetValue(appId, out var umuDir))
            return RuntimeEntryPoint(Path.Combine(home, ".local", "share", "umu", umuDir));

        return null;
    }

    /// <summary>
    /// "run" is the documented wrapper for running a command in the container; _v2-entry-point is
    /// what Steam itself calls and needs the verb spelled out.
    /// </summary>
    private static string? RuntimeEntryPoint(string runtimeDir)
    {
        foreach (var name in new[] { "run", EntryPointScript })
        {
            var candidate = Path.Combine(runtimeDir, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public const string EntryPointScript = "_v2-entry-point";

    private static string? ReadRequiredRuntimeAppId(string protonDir)
    {
        var manifest = Path.Combine(protonDir, "toolmanifest.vdf");
        if (!File.Exists(manifest)) return null;

        try
        {
            foreach (var line in File.ReadLines(manifest))
            {
                if (!line.Contains("require_tool_appid", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
                var appId = parts.LastOrDefault(p => p.Length > 0 && p.All(char.IsDigit));
                if (appId != null) return appId;
            }
        }
        catch (IOException)
        {
            // Unreadable manifest: treat the build as needing no runtime.
        }

        return null;
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

            var install = new ProtonInstall(name, dir, script)
            {
                RequiredRuntimeAppId = ReadRequiredRuntimeAppId(dir)
            };
            if (into.TryGetValue(name, out var existing) && Rank(existing) >= Rank(install))
                continue;

            into[name] = install;
        }
    }
}
