namespace GameLauncher.Tests;

using GameLauncher.Core.Models;
using Xunit;

public class NextcloudConfigTests
{
    private static NextcloudConfig Config(string root = "") => new NextcloudConfig(
        ShareUrl: "https://cloud.example.com/s/AbCdEf123",
        ShareToken: "AbCdEf123"
    ) { RootFolder = root };

    [Fact]
    public void ServerBase_ExtractsSchemeHostAndPort()
    {
        var config = new NextcloudConfig("https://cloud.example.com:8443/s/token", "");

        Assert.Equal("https://cloud.example.com:8443", config.ServerBase);
    }

    [Fact]
    public void ServerBase_OmitDefaultPort()
    {
        var config = new NextcloudConfig("https://cloud.example.com/s/token", "");

        Assert.Equal("https://cloud.example.com", config.ServerBase);
    }

    [Fact]
    public void DavToken_FromShareTokenField()
    {
        Assert.Equal("AbCdEf123", Config().DavToken);
    }

    [Fact]
    public void DavToken_ExtractedFromShareUrl_WhenTokenMissing()
    {
        var config = new NextcloudConfig("https://cloud.example.com/s/MyToken123", "");

        Assert.Equal("MyToken123", config.DavToken);
    }

    [Fact]
    public void WebDavBase_WithRootFolder()
    {
        Assert.Equal(
            "https://cloud.example.com/public.php/dav/files/AbCdEf123/Games",
            Config("Games").WebDavBase);
    }

    [Fact]
    public void WebDavBase_WithoutRootFolder()
    {
        Assert.Equal(
            "https://cloud.example.com/public.php/dav/files/AbCdEf123",
            Config().WebDavBase);
    }

    [Fact]
    public void MetadataUrl_PointsToRoot()
    {
        Assert.Equal(
            "https://cloud.example.com/public.php/dav/files/AbCdEf123/metadata.json",
            Config().MetadataUrl);
    }

    [Fact]
    public void GetFileUrl_BuildsPath()
    {
        var url = Config("Games").GetFileUrl("witcher-3/manifest.json");

        Assert.Equal(
            "https://cloud.example.com/public.php/dav/files/AbCdEf123/Games/witcher-3/manifest.json",
            url);
    }

    [Fact]
    public void GetFileUrl_EscapesSpaces()
    {
        var url = Config().GetFileUrl("my game/bin/run game.exe");

        Assert.EndsWith("/my%20game/bin/run%20game.exe", url);
    }
}
