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
        AppSettings settings,
        GameLocalState? localState = null)
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
            ? BuildWine(exePath, workDir, launchArgs, settings, localState)
            : BuildProton(exePath, workDir, launchArgs, settings, localState);
    }

    /// <summary>
    /// True when a file name is an Online-Fix marker such as OnlineFix.ini / OnlineFix64.dll.
    /// </summary>
    public static bool LooksLikeOnlineFixFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (fileName.Equals("OnlineFix.ini", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("SteamOverlay64.dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.StartsWith("OnlineFix", StringComparison.OrdinalIgnoreCase) &&
               (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the stored manifest lists Online-Fix files — no disk scan needed.</summary>
    public static bool LooksLikeOnlineFix(GameManifest? manifest)
    {
        if (manifest?.Files is not { Length: > 0 } files) return false;

        foreach (var file in files)
        {
            var path = file.Path.Replace('\\', '/');
            var slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path[(slash + 1)..] : path;
            if (LooksLikeOnlineFixFile(name)) return true;
        }

        return false;
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
                    if (LooksLikeOnlineFixFile(Path.GetFileName(entry))) return true;
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
        AppSettings settings,
        GameLocalState? localState)
    {
        var wine = string.IsNullOrWhiteSpace(settings.WineCommand) ? "wine" : settings.WineCommand.Trim();
        var prefix = NonEmpty(localState?.CompatPrefix)
            ?? (string.IsNullOrWhiteSpace(settings.WinePrefix)
                ? Path.Combine(AppPaths.DataDirectory, "wineprefix")
                : settings.WinePrefix.Trim());

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
        ApplyOnlineFixEnvironment(psi, workDir, exePath, onlineFix: LooksLikeOnlineFix(workDir, exePath));
        return psi;
    }

    private static ProcessStartInfo BuildProton(
        string exePath,
        string workDir,
        string[]? launchArgs,
        AppSettings settings,
        GameLocalState? localState)
    {
        var protonVersion = NonEmpty(localState?.ProtonVersion) ?? settings.ProtonVersion;
        var proton = ProtonLocator.Resolve(protonVersion)
            ?? throw new InvalidOperationException(
                "No Proton install found. Install GE-Proton (Steam → compatibilitytools.d), " +
                "or switch the Linux backend to Wine in Settings.");

        var onlineFix = LooksLikeOnlineFix(workDir, exePath);
        var steamRoot = ProtonLocator.FindSteamClientRoot();
        var prefix = NonEmpty(localState?.CompatPrefix)
            ?? ResolveProtonPrefix(workDir, exePath, onlineFix);
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
        var umu = onlineFix ? null : ProtonLocator.FindUmuRun();

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
            // A build that names a runtime in toolmanifest.vdf cannot run outside it, so this is not
            // something the user gets to switch off — it only ever produced import errors.
            var runtime = ProtonLocator.FindSteamRuntime(proton);
            if (runtime == null && proton.RequiredRuntimeAppId is { Length: > 0 } runtimeAppId)
            {
                throw new InvalidOperationException(
                    $"{proton.Name} runs only inside {ProtonLocator.DescribeRuntime(runtimeAppId)}, " +
                    "which is not installed. Install that runtime in Steam, or pick a Proton build " +
                    "matching your runtimes in Settings.");
            }

            psi = new ProcessStartInfo
            {
                FileName = runtime ?? proton.ProtonScript,
                WorkingDirectory = workDir,
                UseShellExecute = false
            };
            if (runtime != null)
            {
                // The bare entry point wants the verb up front; the "run" wrapper takes the
                // command as-is.
                if (Path.GetFileName(runtime) == ProtonLocator.EntryPointScript)
                {
                    psi.ArgumentList.Add("--verb=run");
                    psi.ArgumentList.Add("--");
                }

                psi.ArgumentList.Add(proton.ProtonScript);
            }
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

        ApplyOnlineFixEnvironment(psi, workDir, exePath, steamRoot, onlineFix);
        return psi;
    }

    /// <summary>
    /// Online-Fix: per-game prefix under ~/.local/share/KermoLauncher/prefixes/&lt;key&gt;
    /// (same layout as OFLL, but owned by this launcher).
    /// </summary>
    public static string ResolveProtonPrefix(
        string workDir,
        string exePath,
        bool onlineFix)
    {
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

    private static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        string workDir,
        string exePath,
        string? steamRoot = null,
        bool? onlineFix = null)
    {
        onlineFix ??= LooksLikeOnlineFix(workDir, exePath);
        if (onlineFix != true) return;

        psi.Environment["WINEDLLOVERRIDES"] = OnlineFixDllOverrides;

        steamRoot ??= ProtonLocator.FindSteamClientRoot();
        if (steamRoot == null) return;

        // OFLL enables the real Steam overlay so OnlineFix's SteamOverlay64.dll can resolve.
        psi.Environment["ENABLE_VK_LAYER_VALVE_steam_overlay_1"] = "1";

        // 64-bit Windows games only need the 64-bit overlay (32-bit .so causes ELFCLASS32 noise).
        var overlay64 = Path.Combine(steamRoot, "ubuntu12_64", "gameoverlayrenderer.so");
        if (!File.Exists(overlay64)) return;

        var existing = psi.Environment.TryGetValue("LD_PRELOAD", out var preload)
            ? preload
            : Environment.GetEnvironmentVariable("LD_PRELOAD");
        var parts = new[] { overlay64 }.AsEnumerable();
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
