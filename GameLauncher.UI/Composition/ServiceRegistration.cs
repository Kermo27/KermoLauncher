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
/// Jedyne miejsce, w którym składany jest graf zależności. Wyciągnięte z App, żeby
/// dało się je zweryfikować testem bez uruchamiania Avalonii.
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

        // Typed clients — jedna rejestracja na serwis. Pobrania używają ResponseHeadersRead,
        // więc Timeout ogranicza tylko czas do nagłówków, nie transfer pliku.
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

        // Nazwany klient, bo cache okładek musi żyć w jednym singletonie,
        // a typed client rejestruje się jako transient.
        services.AddHttpClient(nameof(ScreenshotService), c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        services.AddSingleton<IScreenshotService>(sp => new ScreenshotService(
            sp.GetRequiredService<ILocalDbService>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ScreenshotService)),
            sp.GetRequiredService<ILogger<ScreenshotService>>()));

        var maxParallel = startup.Settings.MaxParallelDownloads > 0
            ? startup.Settings.MaxParallelDownloads
            : 2;

        services.AddSingleton<IDownloadService>(sp => new DownloadService(
            sp.GetRequiredService<IWebDavService>(),
            sp.GetRequiredService<ILocalDbService>(),
            sp.GetRequiredService<ILogger<DownloadService>>(),
            maxParallel));

        services.AddSingleton<IGameService, GameService>();
        services.AddSingleton(new AutoUpdateOptions(
            typeof(ServiceRegistration).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            "Kermo27",
            "KermoLauncher"));

        // UI services
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IAppShutdown, AppShutdown>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IUpdateFlowService, UpdateFlowService>();
        services.AddSingleton<IGameItemViewModelFactory, GameItemViewModelFactory>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<LibraryView>();
        services.AddTransient<SettingsView>();

        return services;
    }
}
