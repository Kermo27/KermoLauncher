namespace GameLauncher.Tests;

using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Composition;
using GameLauncher.UI.Services;
using GameLauncher.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The dependency graph used to be checked only by starting the application. These tests catch
/// registration mistakes without putting a window on screen.
/// </summary>
public class CompositionRootTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "gl-di-" + Guid.NewGuid().ToString("N") + ".db");
    private ServiceProvider? _provider;

    public void Dispose()
    {
        _provider?.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLauncherServices(StartupContext.ForTesting(_dbPath), "Tests/1.0");
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        return _provider;
    }

    [Theory]
    [InlineData(typeof(ILocalDbService))]
    [InlineData(typeof(IWebDavService))]
    [InlineData(typeof(IDownloadService))]
    [InlineData(typeof(IGameService))]
    [InlineData(typeof(IAutoUpdateService))]
    [InlineData(typeof(IScreenshotService))]
    [InlineData(typeof(IShellService))]
    [InlineData(typeof(IUpdateFlowService))]
    [InlineData(typeof(IGameItemViewModelFactory))]
    [InlineData(typeof(LibraryViewModel))]
    [InlineData(typeof(SettingsViewModel))]
    [InlineData(typeof(OnboardingViewModel))]
    [InlineData(typeof(MainWindowViewModel))]
    public void EveryService_Resolves(Type serviceType)
    {
        var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    [Fact]
    public void WebDavService_IsRegisteredOnce()
    {
        var services = new ServiceCollection();
        services.AddLauncherServices(StartupContext.ForTesting(_dbPath), "Tests/1.0");

        // AddSingleton and AddHttpClient used to register the same interface twice, so only the
        // last registration actually took effect.
        Assert.Single(services, d => d.ServiceType == typeof(IWebDavService));
    }

    [Fact]
    public void ScreenshotCache_IsShared()
    {
        var provider = BuildProvider();

        Assert.Same(
            provider.GetRequiredService<IScreenshotService>(),
            provider.GetRequiredService<IScreenshotService>());
    }
}
