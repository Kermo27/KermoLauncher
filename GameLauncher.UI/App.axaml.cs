using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Composition;
using GameLauncher.UI.Services;
using GameLauncher.UI.ViewModels;
using GameLauncher.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI;

public partial class App : Application
{
    public static readonly string UserAgentString =
        $"KermoLauncher/{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    private ServiceProvider? _provider;

    /// <summary>Wypełniany przez Program.Main przed startem Avalonii.</summary>
    public static StartupContext? Startup { get; set; }

    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var startup = Startup ?? throw new InvalidOperationException(
            "StartupContext must be loaded before the Avalonia lifetime starts");

        var services = new ServiceCollection();
        services.AddLauncherServices(startup, UserAgentString);
        _provider = services.BuildServiceProvider();
        Services = _provider;

        // Bez tego wyjątek z porzuconego zadania kończy się cicho w finalizatorze.
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Motyw i język są znane od pierwszej klatki, bez blokowania wątku UI na dysku.
        ApplyTheme(startup.Settings.Theme);
        _provider.GetRequiredService<ILocalizationService>().SetLanguage(startup.Settings.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = _provider.GetRequiredService<MainWindow>();
            var mainVm = _provider.GetRequiredService<MainWindowViewModel>();
            mainWindow.DataContext = mainVm;
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += OnShutdownRequested;

            // Sieć i dysk startują po pokazaniu okna. Hak frameworka jest synchroniczny,
            // więc zadanie leci bez oczekiwania, ale z pełną obsługą wyjątków.
            _ = RunStartupAsync(mainVm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunStartupAsync(MainWindowViewModel mainVm)
    {
        try
        {
            await mainVm.InitializeAsync();
        }
        catch (Exception ex)
        {
            _provider?.GetService<ILogger<App>>()?.LogError(ex, "Startup initialization failed");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _provider?.GetService<ILogger<App>>()?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested -= OnShutdownRequested;
        }

        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        // Kontener zwalnia singletony: przerywa pobrania, zdejmuje subskrypcje ViewModeli
        // i domyka plik logu. Wcześniej przy wyjściu nic z tego się nie działo.
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
}
