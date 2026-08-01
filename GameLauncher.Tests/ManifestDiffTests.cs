namespace GameLauncher.Tests;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using Xunit;

public class ManifestDiffTests
{
    private static GameFile File(string path, long size = 100, string sha = "abc")
        => new GameFile(path, size, sha);

    [Fact]
    public void ComputeFilesToDownload_WhenNoInstalledManifest_ReturnsAllFiles()
    {
        var remote = new GameManifest("1.0.0", 300, [File("a.exe"), File("b.exe")]);

        var result = ManifestDiff.ComputeFilesToDownload(remote, null);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void ComputeFilesToDownload_WhenInstalledMatches_ReturnsNone()
    {
        var remote = new GameManifest("2.0.0", 200, [File("a.exe"), File("b.exe")]);
        var installed = new GameManifest("1.0.0", 200, [File("a.exe"), File("b.exe")]);

        var result = ManifestDiff.ComputeFilesToDownload(remote, installed);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeFilesToDownload_WhenPathCaseDiffers_IsIgnored()
    {
        var remote = new GameManifest("2.0.0", 100, [File("Game/EXE.exe")]);
        var installed = new GameManifest("1.0.0", 100, [File("game/exe.exe")]);

        var result = ManifestDiff.ComputeFilesToDownload(remote, installed);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(false, 100, "abc")]  // same file
    [InlineData(true, 101, "abc")]   // size changed
    [InlineData(true, 100, "abd")]   // hash changed
    public void ComputeFilesToDownload_DetectsChangedFiles(bool expectDownload, long size, string sha)
    {
        var remote = new GameManifest("2.0.0", size, [File("a.exe", size, sha)]);
        var installed = new GameManifest("1.0.0", 100, [File("a.exe", 100, "abc")]);

        var result = ManifestDiff.ComputeFilesToDownload(remote, installed);

        Assert.Equal(expectDownload ? 1 : 0, result.Length);
    }

    [Fact]
    public void ComputeStaleFiles_ReturnsFilesMissingFromRemote()
    {
        var remote = new GameManifest("2.0.0", 100, [File("a.exe")]);
        var installed = new GameManifest("1.0.0", 200, [File("a.exe"), File("old.bin")]);

        var result = ManifestDiff.ComputeStaleFiles(remote, installed);

        var stale = Assert.Single(result);
        Assert.Equal("old.bin", stale.Path);
    }

    [Fact]
    public void ComputeStaleFiles_WhenEverythingPresent_ReturnsNone()
    {
        var remote = new GameManifest("2.0.0", 200, [File("a.exe"), File("b.dll")]);
        var installed = new GameManifest("1.0.0", 200, [File("a.exe"), File("b.dll")]);

        var result = ManifestDiff.ComputeStaleFiles(remote, installed);

        Assert.Empty(result);
    }

    [Fact]
    public void IsSameFile_ComparesPathSizeAndHash()
    {
        Assert.True(ManifestDiff.IsSameFile(File("a.exe"), File("A.EXE")));
        Assert.False(ManifestDiff.IsSameFile(File("a.exe"), File("a.exe", 99)));
        Assert.False(ManifestDiff.IsSameFile(File("a.exe"), File("a.exe", 100, "xyz")));
    }
}
