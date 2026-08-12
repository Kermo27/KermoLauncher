namespace GameLauncher.Core.Services;

using System.Diagnostics;
using System.Text;
using GameLauncher.Core.Models;
using GameLauncher.Core.Utils;

/// <summary>
/// Builds a ProcessStartInfo for a game, wrapping Windows binaries with Wine on Linux.
/// </summary>
public static class GameLaunchHelper
{
    public static ProcessStartInfo Build(
        string exePath,
        string workDir,
        string[]? launchArgs,
        AppSettings settings)
    {
        var args = launchArgs is { Length: > 0 } ? string.Join(" ", launchArgs.Select(Quote)) : "";

        if (OperatingSystem.IsWindows() || !GamePaths.LooksLikeWindowsBinary(exePath))
        {
            return new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDir,
                Arguments = args,
                UseShellExecute = false
            };
        }

        if (!settings.LaunchWindowsGamesWithWine)
        {
            throw new InvalidOperationException(
                "This game is a Windows executable. Enable Wine in Settings, or install a Linux build.");
        }

        var wine = string.IsNullOrWhiteSpace(settings.WineCommand) ? "wine" : settings.WineCommand.Trim();
        var prefix = string.IsNullOrWhiteSpace(settings.WinePrefix)
            ? Path.Combine(AppPaths.DataDirectory, "wineprefix")
            : settings.WinePrefix.Trim();

        Directory.CreateDirectory(prefix);

        var psi = new ProcessStartInfo
        {
            FileName = wine,
            WorkingDirectory = workDir,
            Arguments = string.IsNullOrEmpty(args) ? Quote(exePath) : $"{Quote(exePath)} {args}",
            UseShellExecute = false
        };
        psi.Environment["WINEPREFIX"] = prefix;
        return psi;
    }

    /// <summary>Quotes a single argument for a Unix-style process argument list.</summary>
    public static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\' or '\''))
            return value;

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            if (ch is '"' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
