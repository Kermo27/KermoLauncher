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

public class GameLaunchHelperTests
{
    [Fact]
    public void Build_UsesWineForWindowsExeOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var settings = new AppSettings
        {
            LaunchWindowsGamesWithWine = true,
            WineCommand = "/usr/bin/wine",
            WinePrefix = Path.Combine(Path.GetTempPath(), "gl-wine-" + Guid.NewGuid().ToString("N"))
        };

        var psi = GameLaunchHelper.Build(
            "/games/Demo/game.exe",
            "/games/Demo",
            ["-windowed"],
            settings);

        Assert.Equal("/usr/bin/wine", psi.FileName);
        Assert.Contains("game.exe", psi.Arguments);
        Assert.Contains("-windowed", psi.Arguments);
        Assert.Equal(settings.WinePrefix, psi.Environment["WINEPREFIX"]);
    }

    [Fact]
    public void Build_RunsNativeBinaryDirectly()
    {
        var settings = new AppSettings { LaunchWindowsGamesWithWine = true };
        var psi = GameLaunchHelper.Build("/games/Demo/game", "/games/Demo", null, settings);

        Assert.Equal("/games/Demo/game", psi.FileName);
        Assert.Equal("", psi.Arguments);
    }

    [Fact]
    public void Build_ThrowsWhenWineDisabledForWindowsExeOnUnix()
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
