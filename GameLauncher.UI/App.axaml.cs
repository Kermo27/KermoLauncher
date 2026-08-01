using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;
using GameLauncher.UI.ViewModels;
using GameLauncher.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        ApplyStoredTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            var mainVm = Services.GetRequiredService<MainWindowViewModel>();
            mainWindow.DataContext = mainVm;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is not App app) return;
        app.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void ApplyStoredTheme()
    {
        try
        {
            var db = Services!.GetRequiredService<ILocalDbService>();
            var settings = db.GetSettingsAsync().GetAwaiter().GetResult();
            ApplyTheme(settings.Theme);
        }
        catch
        {
            // Fall back to system theme
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));

        // Core services
        services.AddSingleton<ILocalDbService, LocalDbService>();
        services.AddSingleton<IWebDavService, WebDavService>();
        services.AddSingleton<IDownloadService>(sp =>
        {
            var db = sp.GetRequiredService<ILocalDbService>();
            var maxParallel = 2;
            try
            {
                var settings = db.GetSettingsAsync().GetAwaiter().GetResult();
                if (settings.MaxParallelDownloads > 0) maxParallel = settings.MaxParallelDownloads;
            }
            catch
            {
                // Keep default
            }
            return new DownloadService(
                sp.GetRequiredService<IWebDavService>(),
                db,
                sp.GetRequiredService<ILogger<DownloadService>>(),
                maxParallel);
        });
        services.AddSingleton<IGameService, GameService>();
        services.AddSingleton<IAutoUpdateService>(sp => new AutoUpdateService(
            new HttpClient(),
            sp.GetRequiredService<ILogger<AutoUpdateService>>(),
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            "Kermo27",
            "KermoLauncher"
        ));

        // HTTP Client for WebDAV
        services.AddHttpClient<IWebDavService, WebDavService>();

        // UI Services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IScreenshotService>(sp => new ScreenshotService(
            sp.GetRequiredService<ILocalDbService>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<ILogger<ScreenshotService>>()));

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<DownloadsViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
    }
}