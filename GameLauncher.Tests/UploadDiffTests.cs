namespace GameLauncher.Tests;

using GameLauncher.AdminTool.Services;
using GameLauncher.Core.Models;
using Xunit;

public class UploadDiffTests
{
    private static GameFile File(string path, long size = 100, string sha = "abc")
        => new GameFile(path, size, sha);

    [Fact]
    public void FilesToUpload_WhenNoRemoteManifest_UploadsAll()
    {
        var local = new[] { File("a.exe"), File("b.dll") };

        var result = UploadDiff.FilesToUpload(local, null);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void FilesToUpload_SkipsFilesMatchingRemoteManifest()
    {
        var local = new[] { File("a.exe"), File("b.dll") };
        var remote = new GameManifest("1.0.0", 100, [File("a.exe")]);

        var result = UploadDiff.FilesToUpload(local, remote);

        var file = Assert.Single(result);
        Assert.Equal("b.dll", file.Path);
    }

    [Fact]
    public void FilesToUpload_UploadsChangedContent_EvenWhenSizeIsSame()
    {
        var local = new[] { File("a.exe", 100, "newhash") };
        var remote = new GameManifest("1.0.0", 100, [File("a.exe", 100, "oldhash")]);

        var result = UploadDiff.FilesToUpload(local, remote);

        var file = Assert.Single(result);
        Assert.Equal("newhash", file.Sha256);
    }

    [Fact]
    public void FilesToUpload_UploadsWhenSizeDiffers()
    {
        var local = new[] { File("a.exe", 200, "abc") };
        var remote = new GameManifest("1.0.0", 100, [File("a.exe", 100, "abc")]);

        var result = UploadDiff.FilesToUpload(local, remote);

        Assert.Single(result);
    }

    [Fact]
    public void FilesToUpload_PathComparisonIsCaseInsensitive()
    {
        var local = new[] { File("Data/Level.bin", 100, "abc") };
        var remote = new GameManifest("1.0.0", 100, [File("data/level.bin", 100, "abc")]);

        var result = UploadDiff.FilesToUpload(local, remote);

        Assert.Empty(result);
    }
}
