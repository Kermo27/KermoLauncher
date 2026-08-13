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
            WinePrefix = Path.Combine(Path.GetTempPath(), "gl-wine-" + Guid.NewGuid().ToString("N")),
            WineDllOverrides = "OnlineFix64=n"
        };

        var psi = GameLaunchHelper.Build(
            "/games/Demo/game.exe",
            "/games/Demo",
            ["-windowed"],
            settings);

        Assert.Equal("/usr/bin/wine", psi.FileName);
        Assert.Equal(new[] { "/games/Demo/game.exe", "-windowed" }, psi.ArgumentList.ToArray());
        Assert.Equal(settings.WinePrefix, psi.Environment["WINEPREFIX"]);
        Assert.Equal("OnlineFix64=n", psi.Environment["WINEDLLOVERRIDES"]);
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

        // Point HOME so discovery finds our fake Proton; PreferUmuRun=false avoids depending on host umu.
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);

            var settings = new AppSettings
            {
                LaunchWindowsGamesWithWine = true,
                LinuxCompatBackend = GameLaunchHelper.BackendProton,
                ProtonVersion = "GE-Proton99-launch",
                PreferUmuRun = false,
                UseSteamRuntime = false,
                ProtonPrefix = Path.Combine(Path.GetTempPath(), "gl-pfx-" + Guid.NewGuid().ToString("N"))
            };

            var psi = GameLaunchHelper.Build("/games/Demo/game.exe", "/games/Demo", ["-novid"], settings);

            Assert.Equal(protonScript, psi.FileName);
            Assert.Equal(new[] { "run", "/games/Demo/game.exe", "-novid" }, psi.ArgumentList.ToArray());
            Assert.Equal(settings.ProtonPrefix, psi.Environment["STEAM_COMPAT_DATA_PATH"]);
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
        Directory.CreateDirectory(protonDir);
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(protonDir, "proton"), "#!/bin/sh\n");
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

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("quote\"me", "\"quote\\\"me\"")]
    public void Quote_EscapesAsNeeded(string input, string expected)
    {
        Assert.Equal(expected, GameLaunchHelper.Quote(input));
    }
}
