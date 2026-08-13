namespace GameLauncher.Core.Services;

using System.Diagnostics;
using System.Text;
using GameLauncher.Core.Models;
using GameLauncher.Core.Utils;

/// <summary>
/// Builds a ProcessStartInfo for a game. On Linux, Windows binaries go through Proton
/// (umu-run or Steam Runtime + proton, SOFL-style) or plain Wine.
/// </summary>
public static class GameLaunchHelper
{
    public const string BackendProton = "Proton";
    public const string BackendWine = "Wine";

    /// <summary>
    /// OFLL / SOFL WINEDLLOVERRIDES: DXVK d3d* natives + Online-Fix DLL set.
    /// </summary>
    public const string OnlineFixDllOverrides =
        "d3d11=n;d3d10=n;d3d10core=n;dxgi=n;openvr_api_dxvk=n;d3d12=n;d3d12core=n;d3d9=n;d3d8=n;" +
        "onlinefix64=n;steam_api64=n;steamoverlay64=n;winmm=n,b;winhttp=n,b";

    /// <summary>Steam SpaceWar AppID — Online-Fix multiplayer hooks expect this.</summary>
    public const string OnlineFixGameId = "480";

    /// <summary>Legacy next-to-game prefix folder from earlier builds.</summary>
    public const string OnlineFixPrefixFolderName = "OFME Prefix";

    public static ProcessStartInfo Build(
        string exePath,
        string workDir,
        string[]? launchArgs,
        AppSettings settings)
    {
        if (OperatingSystem.IsWindows() || !GamePaths.LooksLikeWindowsBinary(exePath))
        {
            var native = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDir,
                UseShellExecute = false
            };
            AppendArgs(native, launchArgs);
            return native;
        }

        if (!settings.LaunchWindowsGamesWithWine)
        {
            throw new InvalidOperationException(
                "This game is a Windows executable. Enable Proton/Wine in Settings, or install a Linux build.");
        }

        var backend = NormalizeBackend(settings.LinuxCompatBackend);
        return backend == BackendWine
            ? BuildWine(exePath, workDir, launchArgs, settings)
            : BuildProton(exePath, workDir, launchArgs, settings);
    }

    /// <summary>
    /// True when the game folder (or exe dir) contains Online-Fix markers
    /// such as OnlineFix64.dll / OnlineFix.ini.
    /// </summary>
    public static bool LooksLikeOnlineFix(string workDir, string exePath)
    {
        foreach (var dir in CandidateDirs(workDir, exePath))
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals("OnlineFix.ini", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("SteamOverlay64.dll", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (name.StartsWith("OnlineFix", StringComparison.OrdinalIgnoreCase) &&
                        (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch (IOException)
            {
                // Ignore unreadable folders; detection is best-effort.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    public static string NormalizeBackend(string? backend) =>
        string.Equals(backend, BackendWine, StringComparison.OrdinalIgnoreCase)
            ? BackendWine
            : BackendProton;

    private static ProcessStartInfo BuildWine(
        string exePath,
        string workDir,
        string[]? launchArgs,
        AppSettings settings)
    {
        var wine = string.IsNullOrWhiteSpace(settings.WineCommand) ? "wine" : settings.WineCommand.Trim();
        var prefix = string.IsNullOrWhiteSpace(settings.WinePrefix)
            ? Path.Combine(AppPaths.DataDirectory, "wineprefix")
            : settings.WinePrefix.Trim();

        Directory.CreateDirectory(prefix);

        var psi = new ProcessStartInfo
        {
            FileName = wine,
            WorkingDirectory = workDir,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(exePath);
        AppendArgs(psi, launchArgs);
        psi.Environment["WINEPREFIX"] = prefix;
        ApplyOnlineFixEnvironment(psi, settings, workDir, exePath, onlineFix: LooksLikeOnlineFix(workDir, exePath));
        return psi;
    }

    private static ProcessStartInfo BuildProton(
        string exePath,
        string workDir,
        string[]? launchArgs,
        AppSettings settings)
    {
        var proton = ProtonLocator.Resolve(settings.ProtonVersion)
            ?? throw new InvalidOperationException(
                "No Proton install found. Install GE-Proton (Steam → compatibilitytools.d), " +
                "or switch the Linux backend to Wine in Settings.");

        var onlineFix = LooksLikeOnlineFix(workDir, exePath);
        var steamRoot = ProtonLocator.FindSteamClientRoot();
        var prefix = ResolveProtonPrefix(workDir, exePath, settings, onlineFix);
        Directory.CreateDirectory(prefix);
        // Proton/OFLL layout: STEAM_COMPAT_DATA_PATH/<pfx>/drive_c/...
        var winePrefix = Path.Combine(prefix, "pfx");
        Directory.CreateDirectory(winePrefix);

        if (onlineFix)
        {
            EnsureSteamClientDlls(workDir, steamRoot);
            EnsureSteamAppIdFile(workDir);
        }

        // Online-Fix: match OFLL — Steam Runtime + proton run. umu remains for other Windows games.
        var useUmu = settings.PreferUmuRun && !onlineFix;
        var umu = useUmu ? ProtonLocator.FindUmuRun() : null;

        ProcessStartInfo psi;
        if (umu != null)
        {
            psi = new ProcessStartInfo
            {
                FileName = umu,
                WorkingDirectory = workDir,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(exePath);
            AppendArgs(psi, launchArgs);
            psi.Environment["PROTONPATH"] = proton.Directory;
            psi.Environment["WINEPREFIX"] = winePrefix;
            psi.Environment["STEAM_COMPAT_DATA_PATH"] = prefix;
            psi.Environment["GAMEID"] = "0";
        }
        else
        {
            // OFLL / SOFL: [steam-runtime] proton run <exe> ...
            var runtime = settings.UseSteamRuntime ? ProtonLocator.FindSteamRuntime() : null;
            psi = new ProcessStartInfo
            {
                FileName = runtime ?? proton.ProtonScript,
                WorkingDirectory = workDir,
                UseShellExecute = false
            };
            if (runtime != null)
                psi.ArgumentList.Add(proton.ProtonScript);
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(exePath);
            AppendArgs(psi, launchArgs);

            psi.Environment["STEAM_COMPAT_DATA_PATH"] = prefix;
            // Some tools also read WINEPREFIX; keep it aligned with Proton's pfx.
            psi.Environment["WINEPREFIX"] = winePrefix;
        }

        if (steamRoot != null)
        {
            // OFLL uses ~/.steam/steam; resolve that when present so Steam IPC matches.
            var clientInstall = Path.Combine(
                Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".steam", "steam");
            psi.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] =
                Directory.Exists(clientInstall) ? clientInstall : steamRoot;
        }

        if (onlineFix)
        {
            psi.Environment["SteamAppId"] = OnlineFixGameId;
            psi.Environment["SteamGameId"] = OnlineFixGameId;
            psi.Environment["SteamOverlayGameId"] = OnlineFixGameId;
            psi.Environment["SteamAppID"] = OnlineFixGameId;
        }

        ApplyOnlineFixEnvironment(psi, settings, workDir, exePath, steamRoot, onlineFix);
        return psi;
    }

    /// <summary>
    /// Online-Fix: per-game prefix under ~/.local/share/KermoLauncher/prefixes/&lt;key&gt;
    /// (same layout as OFLL, but owned by this launcher). Settings.ProtonPrefix wins.
    /// </summary>
    public static string ResolveProtonPrefix(
        string workDir,
        string exePath,
        AppSettings settings,
        bool onlineFix)
    {
        if (!string.IsNullOrWhiteSpace(settings.ProtonPrefix))
            return settings.ProtonPrefix.Trim();

        if (onlineFix)
        {
            if (!string.IsNullOrWhiteSpace(workDir))
            {
                var legacy = Path.Combine(workDir, OnlineFixPrefixFolderName);
                if (Directory.Exists(Path.Combine(legacy, "pfx")))
                    return legacy;
            }

            var key = PrefixKey(workDir, exePath);
            return Path.Combine(AppPaths.DataDirectory, "prefixes", key);
        }

        return Path.Combine(AppPaths.DataDirectory, "protonprefix");
    }

    /// <summary>Stable per-game prefix folder name (exe stem, e.g. ShiftAtMidnight).</summary>
    public static string PrefixKey(string workDir, string exePath)
    {
        var exeStem = Path.GetFileNameWithoutExtension(exePath);
        if (!string.IsNullOrWhiteSpace(exeStem))
            return exeStem.Trim();

        if (!string.IsNullOrWhiteSpace(workDir))
        {
            var folder = Path.GetFileName(workDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folder))
                return folder.Replace(" ", "", StringComparison.Ordinal);
        }

        return "default";
    }

    /// <summary>
    /// Error 126 (Steam Overlay / steamclient) — OnlineFix loads steamclient64 from the game dir.
    /// Symlink Steam's copies when missing (same idea as many OFLL setups).
    /// </summary>
    public static void EnsureSteamClientDlls(string workDir, string? steamRoot = null)
    {
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
            return;

        steamRoot ??= ProtonLocator.FindSteamClientRoot();
        if (steamRoot == null) return;

        foreach (var name in new[] { "steamclient64.dll", "steamclient.dll" })
        {
            var dest = Path.Combine(workDir, name);
            if (File.Exists(dest) || Directory.Exists(dest))
                continue;

            foreach (var src in new[]
                     {
                         Path.Combine(steamRoot, "legacycompat", name),
                         Path.Combine(steamRoot, name)
                     })
            {
                if (!File.Exists(src)) continue;
                try
                {
                    File.CreateSymbolicLink(dest, src);
                }
                catch
                {
                    try { File.Copy(src, dest, overwrite: false); }
                    catch { /* best-effort */ }
                }

                break;
            }
        }
    }

    /// <summary>True when a Steam client process is running (pidof steam).</summary>
    public static bool IsSteamRunning()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "pidof",
                Arguments = "steam",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes steam_appid.txt = 480 so Online-Fix / SteamAPI resolve SpaceWar.</summary>
    public static void EnsureSteamAppIdFile(string workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
            return;

        var path = Path.Combine(workDir, "steam_appid.txt");
        try
        {
            var desired = OnlineFixGameId + "\n";
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing == OnlineFixGameId) return;
            }

            File.WriteAllText(path, desired);
        }
        catch
        {
            // Best-effort; launch may still work via env SteamAppId.
        }
    }

    private static void ApplyOnlineFixEnvironment(
        ProcessStartInfo psi,
        AppSettings settings,
        string workDir,
        string exePath,
        string? steamRoot = null,
        bool? onlineFix = null)
    {
        onlineFix ??= LooksLikeOnlineFix(workDir, exePath);

        // User override wins; otherwise auto-apply the OFLL set when Online-Fix markers exist.
        if (!string.IsNullOrWhiteSpace(settings.WineDllOverrides))
            psi.Environment["WINEDLLOVERRIDES"] = settings.WineDllOverrides.Trim();
        else if (onlineFix == true)
            psi.Environment["WINEDLLOVERRIDES"] = OnlineFixDllOverrides;

        if (onlineFix != true) return;

        steamRoot ??= ProtonLocator.FindSteamClientRoot();
        if (steamRoot == null) return;

        // OFLL enables the real Steam overlay so OnlineFix's SteamOverlay64.dll can resolve.
        psi.Environment["ENABLE_VK_LAYER_VALVE_steam_overlay_1"] = "1";

        var overlayBits = new[]
        {
            Path.Combine(steamRoot, "ubuntu12_64", "gameoverlayrenderer.so"),
            Path.Combine(steamRoot, "ubuntu12_32", "gameoverlayrenderer.so")
        }.Where(File.Exists).ToArray();

        if (overlayBits.Length == 0) return;

        var existing = psi.Environment.TryGetValue("LD_PRELOAD", out var preload)
            ? preload
            : Environment.GetEnvironmentVariable("LD_PRELOAD");
        var parts = overlayBits.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(existing))
            parts = parts.Concat(existing.Split(':', StringSplitOptions.RemoveEmptyEntries));
        psi.Environment["LD_PRELOAD"] = string.Join(':', parts.Distinct());
    }

    private static IEnumerable<string> CandidateDirs(string workDir, string exePath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(workDir))
        {
            var full = Path.GetFullPath(workDir);
            if (seen.Add(full))
                yield return full;
        }

        var exeDir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            var full = Path.GetFullPath(exeDir);
            if (seen.Add(full))
                yield return full;
        }
    }

    private static void AppendArgs(ProcessStartInfo psi, string[]? launchArgs)
    {
        if (launchArgs is not { Length: > 0 }) return;
        foreach (var arg in launchArgs)
            psi.ArgumentList.Add(arg);
    }

    /// <summary>Quotes a single argument for a Unix-style process argument list.</summary>
    public static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\' or '\''))
            return value;

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            if (ch is '"' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
