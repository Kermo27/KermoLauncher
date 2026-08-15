using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.Core.Utils;
using GameLauncher.UI.Services;
using GameLauncher.UI.ViewModels;
using GameLauncher.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI.Composition;

/// <summary>
/// The single place where the dependency graph is put together. Pulled out of App so a test
/// can verify it without starting Avalonia.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddLauncherServices(
        this IServiceCollection services,
        StartupContext startup,
        string userAgent)
    {
        services.AddLogging(builder =>
        {
            builder.AddDebug().SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(Path.Combine(AppPaths.DataDirectory, "launcher.log")));
        });

        // Core services
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ILocalDbService>(startup.Db);

        // Typed clients, one registration per service. Downloads use ResponseHeadersRead, so
        // Timeout only caps the time to headers, not the transfer itself.
        services.AddHttpClient<IWebDavService, WebDavService>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(120);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        services.AddHttpClient<IAutoUpdateService, AutoUpdateService>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });

        // A named client, because the cover cache has to live in a single singleton while a
        // typed client would be registered as transient.
        services.AddHttpClient(nameof(ScreenshotService), c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        services.AddSingleton<IScreenshotService>(sp => new ScreenshotService(
            sp.GetRequiredService<ILocalDbService>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ScreenshotService)),
            sp.GetRequiredService<ILogger<ScreenshotService>>()));

        // The parallel download limit is read from settings per download, so changing it in
        // Settings takes effect without a restart.
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IGameService, GameService>();
        services.AddSingleton(new AutoUpdateOptions(
            typeof(ServiceRegistration).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            "Kermo27",
            "KermoLauncher"));

        // UI services
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IAppShutdown, AppShutdown>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IShellService, DesktopShellService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IUpdateFlowService, UpdateFlowService>();
        services.AddSingleton<IGameItemViewModelFactory, GameItemViewModelFactory>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<OnboardingViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<LibraryView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<OnboardingView>();

        return services;
    }
}
