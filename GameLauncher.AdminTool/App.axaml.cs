using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using GameLauncher.AdminTool.Services;
using GameLauncher.AdminTool.ViewModels;
using GameLauncher.AdminTool.Views;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.AdminTool;

public partial class App : Application
{
    private ServiceProvider? _provider;

    /// <summary>Filled in by Program.Main before the Avalonia lifetime starts.</summary>
    public static (LocalDbService Db, AppSettings Settings)? Startup { get; set; }

    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _provider = services.BuildServiceProvider();
        Services = _provider;

        ApplyStoredSettings();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = _provider.GetRequiredService<MainWindow>();
            var mainVm = _provider.GetRequiredService<MainViewModel>();
            mainWindow.DataContext = mainVm;
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested -= OnShutdownRequested;
        }

        _provider?.Dispose();
        _provider = null;
        Services = null;
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
        var settings = Startup?.Settings ?? new AppSettings();
        ApplyTheme(settings.Theme);
        _provider!.GetRequiredService<ILocalizationService>().SetLanguage(settings.Language);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));

        services.AddSingleton<ILocalizationService, LocalizationService>();
        if (Startup is { } startup)
        {
            services.AddSingleton<ILocalDbService>(startup.Db);
        }
        else
        {
            services.AddSingleton<ILocalDbService, LocalDbService>();
        }

        services.AddSingleton<IFolderPicker, FolderPicker>();
        services.AddSingleton<MetadataGenerator>();
        services.AddSingleton<LibraryPublisher>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<GameEditorViewModel>();
        services.AddSingleton<UploadViewModel>();

        // Views: the ViewLocator resolves them from here, so every view must be registered.
        services.AddSingleton<MainWindow>();
        services.AddTransient<GameEditorView>();
        services.AddTransient<UploadView>();
    }
}