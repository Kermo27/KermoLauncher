using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using GameLauncher.AdminTool.Services;
using GameLauncher.AdminTool.ViewModels;
using GameLauncher.AdminTool.Views;
using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.AdminTool;

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

        ApplyStoredSettings();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            var mainVm = Services.GetRequiredService<MainViewModel>();
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

    private void ApplyStoredSettings()
    {
        try
        {
            var db = Services!.GetRequiredService<ILocalDbService>();
            var settings = db.GetSettingsAsync().GetAwaiter().GetResult();
            ApplyTheme(settings.Theme);
            Services!.GetRequiredService<ILocalizationService>().SetLanguage(settings.Language);
        }
        catch
        {
            // Fall back to system theme/language
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));

        // Core services (need write access to Nextcloud)
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ILocalDbService, LocalDbService>();
        services.AddSingleton<IWebDavService, WebDavService>();
        services.AddHttpClient<IWebDavService, WebDavService>();

        // Admin services
        services.AddSingleton<MetadataGenerator>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<GameEditorViewModel>();
        services.AddSingleton<UploadViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
    }
}