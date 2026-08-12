namespace GameLauncher.Core.Utils;

/// <summary>
/// Redacts share tokens from URLs before they go into logs. A leaked launcher.log must not
/// contain the private Nextcloud link.
/// </summary>
public static class UrlSanitizer
{
    public static string Mask(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? "";

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return MaskByPathHeuristics(url);

            var builder = new UriBuilder(uri);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.None);
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i].Length == 0) continue;

                // /s/<token>
                if (segments[i].Equals("s", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < segments.Length && segments[i + 1].Length > 0)
                {
                    segments[i + 1] = "***";
                    i++;
                    continue;
                }

                // /public.php/dav/files/<token>/...
                if (segments[i].Equals("files", StringComparison.OrdinalIgnoreCase) &&
                    i > 0 &&
                    segments[i - 1].Equals("dav", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < segments.Length && segments[i + 1].Length > 0)
                {
                    segments[i + 1] = "***";
                    i++;
                }
            }

            builder.Path = string.Join('/', segments);
            builder.Query = uri.Query.TrimStart('?');
            return builder.Uri.ToString();
        }
        catch
        {
            return MaskByPathHeuristics(url);
        }
    }

    private static string MaskByPathHeuristics(string url)
    {
        // Fallback when the string is not a well-formed absolute URI.
        return System.Text.RegularExpressions.Regex.Replace(
            url,
            @"(/s/|/dav/files/)([^/?#\s]+)",
            "$1***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
