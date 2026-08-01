namespace GameLauncher.Tests;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using Xunit;

public class GameUrlTests
{
    private static readonly NextcloudConfig Config = new NextcloudConfig(
        ShareUrl: "https://cloud.example.com/s/token",
        ShareToken: "token");

    [Fact]
    public void GetFileUrl_WhenManifestInSubfolder_PrependsGameFolder()
    {
        var url = GameUrl.GetFileUrl(Config, "test/manifest.json", "testujemy uwu.txt");

        Assert.Equal(
            "https://cloud.example.com/public.php/dav/files/token/test/testujemy%20uwu.txt",
            url);
    }

    [Fact]
    public void GetFileUrl_WhenManifestInRoot_DoesNotPrependFolder()
    {
        var url = GameUrl.GetFileUrl(Config, "manifest.json", "game.exe");

        Assert.Equal(
            "https://cloud.example.com/public.php/dav/files/token/game.exe",
            url);
    }

    [Fact]
    public void GetFileUrl_PreservesSubfolders()
    {
        var url = GameUrl.GetFileUrl(Config, "My Game/manifest.json", "data/level.dat");

        Assert.EndsWith("/My%20Game/data/level.dat", url);
    }

    [Fact]
    public void GetFileUrl_HandlesBackslashManifestUrl()
    {
        var url = GameUrl.GetFileUrl(Config, "test\\manifest.json", "a.bin");

        Assert.EndsWith("/test/a.bin", url);
    }
}
