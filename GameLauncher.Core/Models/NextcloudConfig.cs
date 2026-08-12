namespace GameLauncher.Core.Models;

public record NextcloudConfig(
    string ShareUrl,
    string ShareToken
)
{
    /// <summary>Base folder inside the share: "" when metadata.json sits in the root, "Games" when in a subfolder. Detected automatically.</summary>
    public string RootFolder { get; init; } = "";

    /// <summary>Server taken from the share link (scheme://host[:port]).</summary>
    public string ServerBase
    {
        get
        {
            if (Uri.TryCreate(ShareUrl, UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Host}" + (uri.IsDefaultPort ? "" : $":{uri.Port}");
            }
            return ShareUrl.TrimEnd('/');
        }
    }

    /// <summary>WebDAV token: from the ShareToken field or extracted from the link (/s/&lt;token&gt;).</summary>
    public string DavToken
    {
        get
        {
            var token = ShareToken?.Trim();
            if (!string.IsNullOrWhiteSpace(token)) return token;

            if (Uri.TryCreate(ShareUrl, UriKind.Absolute, out var uri))
            {
                var segments = uri.Segments;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    if (segments[i].TrimEnd('/') == "s" && segments[i + 1].Length > 0)
                    {
                        return segments[i + 1].TrimEnd('/');
                    }
                }
            }
            return "";
        }
    }

    /// <summary>WebDAV base of the public share: https://server/public.php/dav/files/&lt;token&gt;[/RootFolder]</summary>
    public string WebDavBase
    {
        get
        {
            var baseUrl = $"{ServerBase}/public.php/dav/files/{DavToken}";
            return RootFolder.Length > 0 ? $"{baseUrl}/{RootFolder}" : baseUrl;
        }
    }

    public string MetadataUrl => $"{WebDavBase}/metadata.json";

    public string GetFileUrl(string relativePath) => $"{WebDavBase}/{EscapePath(relativePath)}";

    private static string EscapePath(string path)
    {
        return string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
    }
}

public class AppSettings
{
    public NextcloudConfig? Nextcloud { get; set; }
    public string InstallFolder { get; set; } = "";
    public int MaxParallelDownloads { get; set; } = 2;
    public bool AutoUpdate { get; set; } = true;
    public string Theme { get; set; } = "System";
    public string Language { get; set; } = "System";

    /// <summary>On Linux, run .exe/.bat games through Wine (or Proton pointed at by WineCommand).</summary>
    public bool LaunchWindowsGamesWithWine { get; set; } = true;

    /// <summary>Wine/Proton binary. Empty means "wine" on PATH.</summary>
    public string WineCommand { get; set; } = "wine";

    /// <summary>WINEPREFIX. Empty means &lt;app data&gt;/wineprefix.</summary>
    public string WinePrefix { get; set; } = "";

    /// <summary>A shallow copy is enough because NextcloudConfig is an immutable record.</summary>
    public AppSettings Clone() => new()
    {
        Nextcloud = Nextcloud,
        InstallFolder = InstallFolder,
        MaxParallelDownloads = MaxParallelDownloads,
        AutoUpdate = AutoUpdate,
        Theme = Theme,
        Language = Language,
        LaunchWindowsGamesWithWine = LaunchWindowsGamesWithWine,
        WineCommand = WineCommand,
        WinePrefix = WinePrefix
    };
}