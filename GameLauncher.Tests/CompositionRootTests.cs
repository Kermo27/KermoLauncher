namespace GameLauncher.Tests;

using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Composition;
using GameLauncher.UI.Services;
using GameLauncher.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Graf zależności był dotąd sprawdzany dopiero przy uruchomieniu aplikacji.
/// Te testy wyłapują błędy rejestracji bez potrzeby wystawiania okna.
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
    [InlineData(typeof(IUpdateFlowService))]
    [InlineData(typeof(IGameItemViewModelFactory))]
    [InlineData(typeof(LibraryViewModel))]
    [InlineData(typeof(SettingsViewModel))]
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

        // Wcześniej AddSingleton i AddHttpClient rejestrowały ten sam interfejs dwa razy,
        // więc realnie działała tylko ostatnia rejestracja.
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
