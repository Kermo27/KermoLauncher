namespace GameLauncher.Core.Services;

using System.Runtime.InteropServices;

/// <summary>A file attached to a GitHub release, independent of the shape of their JSON.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl);

/// <summary>
/// Picks the release file for the current platform. Split out of the service because it is the
/// only rule in the whole update flow that can be checked without a network or a real binary swap.
/// </summary>
public static class UpdateAssetMatcher
{
    /// <summary>Name of the checksum file attached to every release.</summary>
    public const string ChecksumAssetName = "SHA256SUMS";

    /// <summary>Archives are first-install packages; swapping a binary will not unpack them.</summary>
    private static readonly string[] NotSwappable =
        [".zip", ".tar.gz", ".tgz", ".txt", ".sha256", ".json", ".msi"];

    public static string CurrentRid => $"{CurrentOs()}-{CurrentArch()}";

    private static string CurrentOs() =>
        OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsMacOS() ? "osx"
        : "linux";

    private static string CurrentArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Returns the file to swap in, or null when the release holds nothing for this platform.
    /// The expected name is KermoLauncher-&lt;version&gt;-&lt;rid&gt;, with a .exe suffix on Windows only.
    /// </summary>
    public static ReleaseAsset? Find(IEnumerable<ReleaseAsset> assets, string rid)
    {
        var candidates = assets.Where(a => IsSwappable(a.Name)).ToArray();
        var isWindows = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        var suffix = isWindows ? $"-{rid}.exe" : $"-{rid}";

        var exact = candidates.FirstOrDefault(
            a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Releases made before the workflow had no architecture suffix, and installs from
        // those versions still need a way to update to a newer one.
        if (isWindows)
        {
            return candidates.FirstOrDefault(
                a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static bool IsSwappable(string name)
    {
        if (name.Contains("admintool", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase)) return false;
        return !NotSwappable.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Comparing versions taken from release tags.</summary>
public static class UpdateVersion
{
    /// <summary>
    /// Accepts "v1.2.3", "1.2.3", "1.2" and tags with a suffix ("v1.2.3-beta.1"), of which only
    /// the numeric part is used. Returns null when the tag holds no version number: Version.Parse
    /// used to throw there, which made the whole update check fail.
    /// </summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var text = tag.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text[1..];

        var cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) text = text[..cut];

        if (!Version.TryParse(text, out var version)) return null;

        // Version("1.2") is lower than Version("1.2.0"), so missing fields have to be filled in.
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }
}

/// <summary>Reads a file in sha256sum format: hash, whitespace, file name.</summary>
public static class ChecksumFile
{
    public static string? Find(string content, string fileName)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            // sha256sum marks binary mode with an asterisk in front of the file name.
            var name = parts[^1].TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }
        }
        return null;
    }
}
