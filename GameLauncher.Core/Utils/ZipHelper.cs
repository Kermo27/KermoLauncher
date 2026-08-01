namespace GameLauncher.Core.Utils;

using System.IO.Compression;
using System.Security.Cryptography;

public static class ZipHelper
{
    public static async Task ExtractAsync(string zipPath, string extractDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(extractDir);
        
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var totalEntries = entries.Count;
            var processed = 0;

            // Detect common root folder (first path component shared by all entries)
            var commonPrefix = GetCommonPathPrefix(entries.Select(e => e.FullName).ToArray());
            
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                
                // Strip common prefix if present
                var relativePath = entry.FullName;
                if (!string.IsNullOrEmpty(commonPrefix) && relativePath.StartsWith(commonPrefix))
                {
                    relativePath = relativePath[commonPrefix.Length..].TrimStart('/');
                }
                
                if (string.IsNullOrEmpty(relativePath))
                {
                    processed++;
                    progress?.Report((double)processed / totalEntries);
                    continue;
                }
                
                var destinationPath = Path.GetFullPath(Path.Combine(extractDir, relativePath));
                
                if (!destinationPath.StartsWith(Path.GetFullPath(extractDir)))
                {
                    throw new InvalidOperationException("Zip entry tries to escape extraction directory");
                }

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    entry.ExtractToFile(destinationPath, true);
                }

                processed++;
                progress?.Report((double)processed / totalEntries);
            }
        }, ct);
    }

    private static string GetCommonPathPrefix(string[] paths)
    {
        if (paths.Length == 0) return "";
        
        var first = paths[0];
        var prefixLen = first.Length;
        
        for (int i = 1; i < paths.Length; i++)
        {
            var p = paths[i];
            int j = 0;
            while (j < prefixLen && j < p.Length && first[j] == p[j])
            {
                j++;
            }
            prefixLen = j;
            if (prefixLen == 0) return "";
        }
        
        // Ensure prefix ends at a directory boundary
        var prefix = first[..prefixLen];
        var lastSlash = prefix.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            prefix = prefix[..(lastSlash + 1)];
        }
        else
        {
            prefix = "";
        }
        
        return prefix;
    }

    public static async Task VerifyChecksumAsync(string filePath, string expectedSha256, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Checksum mismatch. Expected: {expectedSha256}, Got: {actual}");
            }
        }, ct);
    }
}