namespace GameLauncher.Tests;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using GameLauncher.Core.Utils;
using Xunit;

public class UrlSanitizerTests
{
    [Theory]
    [InlineData(
        "https://cloud.example/s/SecretToken123",
        "https://cloud.example/s/***")]
    [InlineData(
        "https://cloud.example/public.php/dav/files/SecretToken123/metadata.json",
        "https://cloud.example/public.php/dav/files/***/metadata.json")]
    [InlineData(
        "https://cloud.example/public.php/dav/files/SecretToken123/Games/cover.jpg",
        "https://cloud.example/public.php/dav/files/***/Games/cover.jpg")]
    public void Mask_RedactsShareTokens(string input, string expected)
    {
        Assert.Equal(expected, UrlSanitizer.Mask(input));
    }

    [Fact]
    public void Mask_LeavesUrlsWithoutTokensAlone()
    {
        const string url = "https://api.github.com/repos/Kermo27/KermoLauncher/releases/latest";
        Assert.Equal(url, UrlSanitizer.Mask(url));
    }
}

public class OnboardingSettingsTests
{
    [Fact]
    public void FreshSettings_NeedOnboarding()
    {
        var settings = new AppSettings();
        Assert.True(settings.NeedsOnboarding);
        Assert.False(settings.OnboardingCompleted);
    }

    [Fact]
    public void CompletedFlag_StopsOnboardingEvenWithoutNextcloud()
    {
        // Clearing the share link in Settings must not reopen the wizard.
        var settings = new AppSettings { OnboardingCompleted = true, Nextcloud = null };
        Assert.False(settings.NeedsOnboarding);
    }

    [Fact]
    public async Task GetSettings_MigratesExistingShareAsCompleted()
    {
        var path = Path.Combine(Path.GetTempPath(), "gl-onboard-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var db = new LocalDbService(path);
            await db.SaveSettingsAsync(new AppSettings
            {
                OnboardingCompleted = false,
                Nextcloud = new NextcloudConfig("https://cloud.example/s/abc", "")
            });

            // Simulate a pre-wizard install: flag missing from JSON is false after deserialize,
            // but a configured share must skip the wizard.
            var raw = await db.GetSettingsAsync();
            // Overwrite cache with the legacy shape.
            await db.SaveSettingsAsync(new AppSettings
            {
                OnboardingCompleted = false,
                Nextcloud = new NextcloudConfig("https://cloud.example/s/abc", "")
            });

            // Clear cache by creating a new service on the same file.
            var reopened = new LocalDbService(path);
            // Force the JSON without the flag by writing it directly... simpler: just assert
            // that GetSettings with Nextcloud and false flag gets migrated.
            var settings = await reopened.GetSettingsAsync();
            Assert.True(settings.OnboardingCompleted);
            Assert.False(settings.NeedsOnboarding);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

public class InstallFolderTests
{
    [Fact]
    public void TryValidate_AcceptsWritableDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gl-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(InstallFolder.TryValidate(dir, out var error, out var free));
            Assert.Null(error);
            Assert.True(free > 0);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void TryValidate_RejectsEmptyPath()
    {
        Assert.False(InstallFolder.TryValidate("  ", out var error, out _));
        Assert.Equal("empty", error);
    }

    [Fact]
    public void GetAvailableBytes_ReturnsPositiveForTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gl-free-" + Guid.NewGuid().ToString("N"));
        try
        {
            var free = InstallFolder.GetAvailableBytes(dir);
            Assert.NotNull(free);
            Assert.True(free > 0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ThrowIfInsufficient_ThrowsWhenNeedExceedsFreeSpace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gl-space-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = Assert.Throws<InsufficientDiskSpaceException>(
                () => InstallFolder.ThrowIfInsufficient(dir, long.MaxValue));
            Assert.Equal(long.MaxValue, ex.RequiredBytes);
            Assert.True(ex.AvailableBytes >= 0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ThrowIfInsufficient_AllowsZeroNeed()
    {
        InstallFolder.ThrowIfInsufficient(Path.GetTempPath(), 0);
    }
}
