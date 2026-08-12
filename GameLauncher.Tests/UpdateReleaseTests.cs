namespace GameLauncher.Tests;

using GameLauncher.Core.Services;
using Xunit;

public class UpdateAssetMatcherTests
{
    private static readonly ReleaseAsset[] FullRelease =
    [
        new("KermoLauncher-1.0.6-win-x64.exe", "https://example.com/win"),
        new("KermoLauncher-1.0.6-linux-x64", "https://example.com/linux"),
        new("KermoLauncher.AdminTool-1.0.6-win-x64.zip", "https://example.com/admin"),
        new("SHA256SUMS", "https://example.com/sums")
    ];

    [Theory]
    [InlineData("win-x64", "https://example.com/win")]
    [InlineData("linux-x64", "https://example.com/linux")]
    public void Find_PicksAssetForRid(string rid, string expectedUrl)
    {
        var asset = UpdateAssetMatcher.Find(FullRelease, rid);

        Assert.NotNull(asset);
        Assert.Equal(expectedUrl, asset!.DownloadUrl);
    }

    [Fact]
    public void Find_IgnoresAdminToolAndChecksums()
    {
        ReleaseAsset[] assets =
        [
            new("SHA256SUMS", "https://example.com/sums"),
            new("KermoLauncher.AdminTool-1.0.6-win-x64.zip", "https://example.com/admin")
        ];

        Assert.Null(UpdateAssetMatcher.Find(assets, "win-x64"));
        Assert.Null(UpdateAssetMatcher.Find(assets, "linux-x64"));
    }

    [Fact]
    public void Find_DoesNotMatchArchiveForLinux()
    {
        // An archive would be swapped in as the binary and leave the install unable to start.
        ReleaseAsset[] assets = [new("KermoLauncher-1.0.6-linux-x64.tar.gz", "https://example.com/tar")];

        Assert.Null(UpdateAssetMatcher.Find(assets, "linux-x64"));
    }

    [Fact]
    public void Find_FallsBackToAnyExeOnWindows()
    {
        // Releases made before the workflow had no architecture suffix.
        ReleaseAsset[] assets = [new("KermoLauncher.exe", "https://example.com/legacy")];

        var asset = UpdateAssetMatcher.Find(assets, "win-x64");

        Assert.Equal("https://example.com/legacy", asset?.DownloadUrl);
    }

    [Fact]
    public void Find_HandlesReleaseMadeBeforeTheWorkflow()
    {
        // The exact asset list of release 1.0.5, which was still put together by hand.
        ReleaseAsset[] assets =
        [
            new("KermoLauncher-1.0.5-win-x64.exe", "https://example.com/win"),
            new("KermoLauncher.AdminTool.zip", "https://example.com/admin")
        ];

        Assert.Equal("https://example.com/win", UpdateAssetMatcher.Find(assets, "win-x64")?.DownloadUrl);
        // Those releases held nothing for Linux, and the Admin Tool zip must not stand in for it.
        Assert.Null(UpdateAssetMatcher.Find(assets, "linux-x64"));
    }

    [Fact]
    public void Find_WrongArchitectureIsNotUsed()
    {
        ReleaseAsset[] assets = [new("KermoLauncher-1.0.6-linux-x64", "https://example.com/x64")];

        Assert.Null(UpdateAssetMatcher.Find(assets, "linux-arm64"));
    }
}

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2.3", "1.2.3.0")]
    [InlineData("V1.2", "1.2.0.0")]
    [InlineData("v1.2.3-beta.1", "1.2.3.0")]
    [InlineData("v1.2.3+build7", "1.2.3.0")]
    public void Parse_AcceptsCommonTagShapes(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateVersion.Parse(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nightly")]
    [InlineData("v")]
    public void Parse_ReturnsNullInsteadOfThrowing(string? tag)
    {
        Assert.Null(UpdateVersion.Parse(tag));
    }

    [Fact]
    public void Parse_TwoAndThreePartVersionsCompareEqual()
    {
        // Version("1.2") < Version("1.2.0") without padding, which would offer an update forever.
        Assert.Equal(UpdateVersion.Parse("1.2"), UpdateVersion.Parse("1.2.0"));
    }
}

public class ChecksumFileTests
{
    private const string Content = """
        # KermoLauncher 1.0.6 checksums
        aaa111  KermoLauncher-1.0.6-win-x64.exe
        bbb222 *KermoLauncher-1.0.6-linux-x64
        """;

    [Theory]
    [InlineData("KermoLauncher-1.0.6-win-x64.exe", "aaa111")]
    [InlineData("KermoLauncher-1.0.6-linux-x64", "bbb222")]
    public void Find_ReadsHashForFile(string fileName, string expected)
    {
        Assert.Equal(expected, ChecksumFile.Find(Content, fileName));
    }

    [Fact]
    public void Find_UnknownFileReturnsNull()
    {
        Assert.Null(ChecksumFile.Find(Content, "KermoLauncher-1.0.6-osx-arm64"));
    }
}
