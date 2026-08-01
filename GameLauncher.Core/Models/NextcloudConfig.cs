namespace GameLauncher.Core.Models;

public record NextcloudConfig(
    string ShareUrl,
    string ShareToken
)
{
    /// <summary>Katalog bazowy w udostępnieniu: "" gdy metadata.json leży w korzeniu, "Games" gdy w podfolderze. Wykrywany automatycznie.</summary>
    public string RootFolder { get; init; } = "";

    /// <summary>Serwer wyciągnięty z linku udostępniania (scheme://host[:port]).</summary>
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

    /// <summary>Token WebDAV: z pola ShareToken albo wyciągnięty z linku (/s/&lt;token&gt;).</summary>
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

    /// <summary>Baza WebDAV publicznego udostępnienia: https://server/public.php/dav/files/&lt;token&gt;[/RootFolder]</summary>
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
    public string DownloadFolder { get; set; } = "";
    public string InstallFolder { get; set; } = "";
    public int MaxParallelDownloads { get; set; } = 2;
    public bool AutoUpdate { get; set; } = true;
    public string Theme { get; set; } = "System";
    public string Language { get; set; } = "System";
}