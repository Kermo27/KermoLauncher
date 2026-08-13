namespace GameLauncher.Tests;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using GameLauncher.Core.Utils;
using Xunit;

public class GamePathsTests
{
    [Fact]
    public void Combine_SplitsWindowsSeparatorsOnAnyHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "games");
        var combined = GamePaths.Combine(root, @"bin\win64\game.exe");

        Assert.Equal(Path.Combine(root, "bin", "win64", "game.exe"), combined);
    }

    [Theory]
    [InlineData("game.exe", true)]
    [InlineData("game.bat", true)]
    [InlineData("game", false)]
    [InlineData("game.sh", false)]
    public void LooksLikeWindowsBinary_UsesExtension(string name, bool expected)
    {
        Assert.Equal(expected, GamePaths.LooksLikeWindowsBinary(name));
    }

    [Fact]
    public void TryMakeExecutable_SetsUserExecuteOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), "gl-exec-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "#!/bin/sh\necho hi\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            GamePaths.TryMakeExecutable(path);

            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void TryMakeExecutable_SkipsWindowsBinaries()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), "gl-exe-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(path, "MZ");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            GamePaths.TryMakeExecutable(path);

            Assert.False(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

public class ProtonLocatorTests
{
    [Fact]
    public void FindInstalled_SeesProtonScriptInFakeHome()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = Path.Combine(Path.GetTempPath(), "gl-home-" + Guid.NewGuid().ToString("N"));
        var protonDir = Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d", "GE-Proton99-test");
        Directory.CreateDirectory(protonDir);
        File.WriteAllText(Path.Combine(protonDir, "proton"), "#!/bin/sh\n");

        try
        {
            var found = ProtonLocator.FindInstalled(home);
            Assert.Contains(found, p => p.Name == "GE-Proton99-test");
            Assert.Equal(Path.Combine(protonDir, "proton"), found.First(p => p.Name == "GE-Proton99-test").ProtonScript);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Resolve_PrefersNamedVersion()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = Path.Combine(Path.GetTempPath(), "gl-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d");
        foreach (var name in new[] { "GE-Proton10-1", "GE-Proton11-2" })
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "proton"), "#!/bin/sh\n");
        }

        try
        {
            var resolved = ProtonLocator.Resolve("GE-Proton10-1", home);
            Assert.NotNull(resolved);
            Assert.Equal("GE-Proton10-1", resolved!.Name);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Resolve_PrefersUserGeProtonOverUsrCachyos()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = Path.Combine(Path.GetTempPath(), "gl-home-" + Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d", "GE-Proton11-5");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "proton"), "#!/bin/sh\n");

        // Simulate a system proton that sorts higher alphabetically but lives under /usr.
        var usr = new ProtonLocator.ProtonInstall(
            "proton-cachyos-slr",
            "/usr/share/steam/compatibilitytools.d/proton-cachyos-slr",
            "/usr/share/steam/compatibilitytools.d/proton-cachyos-slr/proton");
        var ge = new ProtonLocator.ProtonInstall(
            "GE-Proton11-5",
            userDir,
            Path.Combine(userDir, "proton"));

        Assert.True(ProtonLocator.Rank(ge) > ProtonLocator.Rank(usr));

        try
        {
            var resolved = ProtonLocator.Resolve(null, home);
            Assert.NotNull(resolved);
            Assert.Equal("GE-Proton11-5", resolved!.Name);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}

public class GameLaunchHelperTests
{
    [Fact]
    public void Build_UsesWineWhenBackendIsWine()
    {
        if (OperatingSystem.IsWindows()) return;

        var settings = new AppSettings
        {
            LaunchWindowsGamesWithWine = true,
            LinuxCompatBackend = GameLaunchHelper.BackendWine,
            WineCommand = "/usr/bin/wine",
            WinePrefix = Path.Combine(Path.GetTempPath(), "gl-wine-" + Guid.NewGuid().ToString("N"))
        };

        var psi = GameLaunchHelper.Build(
            "/games/Demo/game.exe",
            "/games/Demo",
            ["-windowed"],
            settings);

        Assert.Equal("/usr/bin/wine", psi.FileName);
        Assert.Equal(new[] { "/games/Demo/game.exe", "-windowed" }, psi.ArgumentList.ToArray());
        Assert.Equal(settings.WinePrefix, psi.Environment["WINEPREFIX"]);
        Assert.False(psi.Environment.ContainsKey("WINEDLLOVERRIDES"));
    }

    [Fact]
    public void Build_UsesUmuOrProtonWhenBackendIsProton()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = Path.Combine(Path.GetTempPath(), "gl-home-" + Guid.NewGuid().ToString("N"));
        var protonDir = Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d", "GE-Proton99-launch");
        Directory.CreateDirectory(protonDir);
        var protonScript = Path.Combine(protonDir, "proton");
        File.WriteAllText(protonScript, "#!/bin/sh\n");

        // Point HOME so discovery finds our fake Proton. The build names no runtime in a
        // toolmanifest, so the launch stays on the bare proton script.
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);

            var settings = new AppSettings
            {
                LaunchWindowsGamesWithWine = true,
                LinuxCompatBackend = GameLaunchHelper.BackendProton,
                ProtonVersion = "GE-Proton99-launch"
            };

            var psi = GameLaunchHelper.Build("/games/Demo/game.exe", "/games/Demo", ["-novid"], settings);
            var expectedPrefix = GameLaunchHelper.ResolveProtonPrefix(
                "/games/Demo", "/games/Demo/game.exe", onlineFix: false);

            Assert.Equal(protonScript, psi.FileName);
            Assert.Equal(new[] { "run", "/games/Demo/game.exe", "-novid" }, psi.ArgumentList.ToArray());
            Assert.Equal(expectedPrefix, psi.Environment["STEAM_COMPAT_DATA_PATH"]);
            Assert.False(psi.Environment.ContainsKey("WINEDLLOVERRIDES"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Build_AppliesOnlineFixOverridesWhenMarkersPresent()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = Path.Combine(Path.GetTempPath(), "gl-home-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(Path.GetTempPath(), "gl-game-" + Guid.NewGuid().ToString("N"));
        var protonDir = Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d", "GE-Proton99-of");
        var steamRoot = Path.Combine(home, ".local", "share", "Steam");
        Directory.CreateDirectory(protonDir);
        Directory.CreateDirectory(Path.Combine(steamRoot, "ubuntu12_64"));
        Directory.CreateDirectory(Path.Combine(steamRoot, "ubuntu12_32"));
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(protonDir, "proton"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(steamRoot, "ubuntu12_64", "gameoverlayrenderer.so"), "so64");
        File.WriteAllText(Path.Combine(steamRoot, "ubuntu12_32", "gameoverlayrenderer.so"), "so32");
        File.WriteAllText(Path.Combine(gameDir, "OnlineFix.ini"), "[Main]\n");
        File.WriteAllText(Path.Combine(gameDir, "OnlineFix64.dll"), "x");
        File.WriteAllText(Path.Combine(gameDir, "game.exe"), "MZ");

        var previousHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);

            var settings = new AppSettings
            {
                LaunchWindowsGamesWithWine = true,
                LinuxCompatBackend = GameLaunchHelper.BackendProton,
                ProtonVersion = "GE-Proton99-of",
                PreferUmuRun = true, // Online-Fix must still use proton run (OFLL-style)
                UseSteamRuntime = false
            };

            var exe = Path.Combine(gameDir, "game.exe");
            Assert.True(GameLaunchHelper.LooksLikeOnlineFix(gameDir, exe));

            var psi = GameLaunchHelper.Build(exe, gameDir, null, settings);

            Assert.Equal(GameLaunchHelper.OnlineFixDllOverrides, psi.Environment["WINEDLLOVERRIDES"]);
            Assert.Equal(GameLaunchHelper.OnlineFixGameId, psi.Environment["SteamAppId"]);
            var expectedCompat = Path.Combine(AppPaths.DataDirectory, "prefixes", "game");
            Assert.Equal(expectedCompat, psi.Environment["STEAM_COMPAT_DATA_PATH"]);
            Assert.Equal(Path.Combine(expectedCompat, "pfx"), psi.Environment["WINEPREFIX"]);
            Assert.Equal("1", psi.Environment["ENABLE_VK_LAYER_VALVE_steam_overlay_1"]);
            Assert.Equal(GameLaunchHelper.OnlineFixGameId,
                File.ReadAllText(Path.Combine(gameDir, "steam_appid.txt")).Trim());
            Assert.Equal(Path.Combine(protonDir, "proton"), psi.FileName);
            Assert.Contains("ubuntu12_64/gameoverlayrenderer.so", psi.Environment["LD_PRELOAD"]);
            Assert.DoesNotContain("ubuntu12_32/gameoverlayrenderer.so", psi.Environment["LD_PRELOAD"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            try { Directory.Delete(home, recursive: true); } catch { }
            try { Directory.Delete(gameDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PrefixKey_UsesExeStem()
    {
        Assert.Equal(
            "ShiftAtMidnight",
            GameLaunchHelper.PrefixKey(
                "/games/Shift At Midnight",
                "/games/Shift At Midnight/ShiftAtMidnight.exe"));
    }

    [Fact]
    public void EnsureSteamClientDlls_SymlinksFromSteamRoot()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = Path.Combine(Path.GetTempPath(), "gl-home-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(Path.GetTempPath(), "gl-game-" + Guid.NewGuid().ToString("N"));
        var steam = Path.Combine(home, ".local", "share", "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "legacycompat"));
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(steam, "legacycompat", "steamclient64.dll"), "dll");

        try
        {
            GameLaunchHelper.EnsureSteamClientDlls(gameDir, steam);
            var link = Path.Combine(gameDir, "steamclient64.dll");
            Assert.True(File.Exists(link));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
            try { Directory.Delete(gameDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Build_NonOnlineFixCanStillUseUmu()
    {
        if (OperatingSystem.IsWindows()) return;
        if (ProtonLocator.FindUmuRun() is null) return;

        var home = Path.Combine(Path.GetTempPath(), "gl-home-" + Guid.NewGuid().ToString("N"));
        var protonDir = Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d", "GE-Proton99-umu");
        Directory.CreateDirectory(protonDir);
        File.WriteAllText(Path.Combine(protonDir, "proton"), "#!/bin/sh\n");

        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var pfx = Path.Combine(Path.GetTempPath(), "gl-pfx-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);

            var settings = new AppSettings
            {
                LaunchWindowsGamesWithWine = true,
                LinuxCompatBackend = GameLaunchHelper.BackendProton,
                ProtonVersion = "GE-Proton99-umu",
                PreferUmuRun = true,
                ProtonPrefix = pfx
            };

            // Plain .exe without Online-Fix markers → umu path.
            var psi = GameLaunchHelper.Build("/games/Demo/game.exe", "/games/Demo", null, settings);

            Assert.Contains("umu-run", psi.FileName);
            Assert.Equal(Path.Combine(pfx, "pfx"), psi.Environment["WINEPREFIX"]);
            Assert.Equal(pfx, psi.Environment["STEAM_COMPAT_DATA_PATH"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            try { Directory.Delete(home, recursive: true); } catch { }
            try { Directory.Delete(pfx, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Build_RunsNativeBinaryDirectly()
    {
        var settings = new AppSettings { LaunchWindowsGamesWithWine = true };
        var psi = GameLaunchHelper.Build("/games/Demo/game", "/games/Demo", null, settings);

        Assert.Equal("/games/Demo/game", psi.FileName);
        Assert.Empty(psi.ArgumentList);
    }

    [Fact]
    public void Build_ThrowsWhenCompatDisabledForWindowsExeOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var settings = new AppSettings { LaunchWindowsGamesWithWine = false };

        Assert.Throws<InvalidOperationException>(() =>
            GameLaunchHelper.Build("/games/Demo/game.exe", "/games/Demo", null, settings));
    }

    /// <summary>
    /// Creates a fake home with the given Proton builds (name → required runtime appid) and the
    /// given runtime folders in a second Steam library, the way a real dual-library setup looks.
    /// </summary>
    private static string MakeHomeWithProtons(
        (string Name, string? RuntimeAppId)[] protons,
        string[] runtimeDirs)
    {
        var home = Path.Combine(Path.GetTempPath(), "gl-home-" + Guid.NewGuid().ToString("N"));
        var steamRoot = Path.Combine(home, ".local", "share", "Steam");

        foreach (var (name, runtimeAppId) in protons)
        {
            var dir = Path.Combine(steamRoot, "compatibilitytools.d", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "proton"), "#!/bin/sh\n");
            if (runtimeAppId != null)
            {
                File.WriteAllText(
                    Path.Combine(dir, "toolmanifest.vdf"),
                    "\"manifest\"\n{\n  \"version\" \"2\"\n  \"require_tool_appid\" \"" + runtimeAppId + "\"\n}\n");
            }
        }

        var library = Path.Combine(home, "library");
        foreach (var runtimeDir in runtimeDirs)
        {
            var dir = Path.Combine(library, "steamapps", "common", runtimeDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "run"), "#!/bin/sh\n");
        }

        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        File.WriteAllText(
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + library + "\"\n\t}\n}\n");

        return home;
    }

    [Fact]
    public void FindSteamRuntime_PicksTheRuntimeTheBuildRequires()
    {
        if (OperatingSystem.IsWindows()) return;

        // GE-Proton 11 asks for runtime 4.0; sniper is present too and used to win by accident,
        // which broke the launch on Python 3.9.
        var home = MakeHomeWithProtons(
            [("GE-Proton11-5", "4183110")],
            ["SteamLinuxRuntime_sniper", "SteamLinuxRuntime_4"]);

        try
        {
            var proton = ProtonLocator.Resolve(null, home);
            Assert.NotNull(proton);
            Assert.Equal("4183110", proton!.RequiredRuntimeAppId);

            var runtime = ProtonLocator.FindSteamRuntime(proton, home);
            Assert.NotNull(runtime);
            Assert.Contains(Path.Combine("common", "SteamLinuxRuntime_4"), runtime);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FindSteamRuntime_ReturnsNullWhenRequiredRuntimeIsMissing()
    {
        if (OperatingSystem.IsWindows()) return;

        var home = MakeHomeWithProtons([("GE-Proton11-5", "4183110")], ["SteamLinuxRuntime_sniper"]);

        try
        {
            var proton = ProtonLocator.Resolve(null, home);
            Assert.Null(ProtonLocator.FindSteamRuntime(proton!, home));
            Assert.False(ProtonLocator.HasRequiredRuntime(proton!, home));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Resolve_PrefersBuildWhoseRuntimeIsInstalled()
    {
        if (OperatingSystem.IsWindows()) return;

        // Only sniper installed: the newer build cannot run, so the older, runnable one wins.
        var home = MakeHomeWithProtons(
            [("GE-Proton10-34", "1628350"), ("GE-Proton11-5", "4183110")],
            ["SteamLinuxRuntime_sniper"]);

        try
        {
            var proton = ProtonLocator.Resolve(null, home);
            Assert.Equal("GE-Proton10-34", proton!.Name);

            // An explicit choice in Settings still wins over availability.
            Assert.Equal("GE-Proton11-5", ProtonLocator.Resolve("GE-Proton11-5", home)!.Name);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("has space", "\"has space\"")]
    [InlineData("quote\"me", "\"quote\\\"me\"")]
    public void Quote_EscapesAsNeeded(string input, string expected)
    {
        Assert.Equal(expected, GameLaunchHelper.Quote(input));
    }
}
