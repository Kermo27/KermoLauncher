using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;

namespace GameLauncher.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ILocalDbService _db;
    private readonly IWebDavService _webDav;
    private readonly IAutoUpdateService _autoUpdateService;
    private readonly IUpdateFlowService _updateFlow;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;

    // Settings are spread over observable properties. XAML used to bind straight to AppSettings,
    // which raises no change notifications, so some fields never refreshed the view.
    [ObservableProperty]
    private string _installFolder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShareUrl))]
    private string _shareUrl = "";

    [ObservableProperty]
    private string _shareToken = "";

    [ObservableProperty]
    private int _maxParallelDownloads = 2;

    [ObservableProperty]
    private bool _autoUpdate = true;

    [ObservableProperty]
    private int _selectedThemeIndex;

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private bool _launchWindowsGamesWithWine = true;

    [ObservableProperty]
    private string _wineCommand = "wine";

    [ObservableProperty]
    private string _winePrefix = "";

    public bool HasShareUrl => !string.IsNullOrWhiteSpace(ShareUrl);

    /// <summary>Wine settings only matter when launching Windows binaries on a non-Windows host.</summary>
    public bool ShowWineSettings => !OperatingSystem.IsWindows();

    public string[] ThemeValues { get; } = ["Light", "Dark", "System"];
    public string[] LanguageValues { get; } = ["System", "en", "pl"];

    public string[] ThemeOptions =>
        [L["Settings.Theme.Light"], L["Settings.Theme.Dark"], L["Settings.Theme.System"]];

    public string[] LanguageOptions =>
        [L["Settings.Language.System"], L["Settings.Language.English"], L["Settings.Language.Polish"]];

    public SettingsViewModel(
        ILocalDbService db,
        IWebDavService webDav,
        IAutoUpdateService autoUpdateService,
        IUpdateFlowService updateFlow,
        IDialogService dialogService,
        INotificationService notificationService,
        ILocalizationService localization)
        : base(localization)
    {
        _db = db;
        _webDav = webDav;
        _autoUpdateService = autoUpdateService;
        _updateFlow = updateFlow;
        _dialogService = dialogService;
        _notificationService = notificationService;
    }

    /// <summary>The database read is kept out of the constructor so errors cannot vanish into an unobserved task.</summary>
    public async Task InitializeAsync()
    {
        var settings = await _db.GetSettingsAsync();

        InstallFolder = settings.InstallFolder;
        MaxParallelDownloads = settings.MaxParallelDownloads;
        AutoUpdate = settings.AutoUpdate;
        SelectedThemeIndex = Math.Max(0, Array.IndexOf(ThemeValues, settings.Theme));
        SelectedLanguageIndex = Math.Max(0, Array.IndexOf(LanguageValues, settings.Language));
        LaunchWindowsGamesWithWine = settings.LaunchWindowsGamesWithWine;
        WineCommand = string.IsNullOrWhiteSpace(settings.WineCommand) ? "wine" : settings.WineCommand;
        WinePrefix = settings.WinePrefix;

        if (settings.Nextcloud != null)
        {
            ShareUrl = settings.Nextcloud.ShareUrl;
            ShareToken = settings.Nextcloud.ShareToken;
        }
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(ThemeOptions));
        OnPropertyChanged(nameof(LanguageOptions));
    }

    private string SelectedTheme => ThemeValues[Math.Clamp(SelectedThemeIndex, 0, ThemeValues.Length - 1)];
    private string SelectedLanguage => LanguageValues[Math.Clamp(SelectedLanguageIndex, 0, LanguageValues.Length - 1)];

    [RelayCommand]
    private async Task SaveAsync()
    {
        var shareUrl = ShareUrl?.Trim() ?? "";
        var settings = new AppSettings
        {
            InstallFolder = InstallFolder,
            MaxParallelDownloads = MaxParallelDownloads,
            AutoUpdate = AutoUpdate,
            Theme = SelectedTheme,
            Language = SelectedLanguage,
            LaunchWindowsGamesWithWine = LaunchWindowsGamesWithWine,
            WineCommand = string.IsNullOrWhiteSpace(WineCommand) ? "wine" : WineCommand.Trim(),
            WinePrefix = WinePrefix?.Trim() ?? "",
            Nextcloud = string.IsNullOrWhiteSpace(shareUrl)
                ? null
                : new NextcloudConfig(shareUrl, ShareToken?.Trim() ?? "")
        };

        await _db.SaveSettingsAsync(settings);
        App.ApplyTheme(settings.Theme);
        L.SetLanguage(settings.Language);
        _notificationService.Show(L["Settings.SavedTitle"], L["Settings.SavedMessage"]);
    }

    [RelayCommand]
    private async Task BrowseWinePrefixAsync()
    {
        var folder = await _dialogService.ShowFolderPickerAsync(L["Settings.Wine.BrowsePrefix"], WinePrefix);
        if (!string.IsNullOrEmpty(folder))
        {
            WinePrefix = folder;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _autoUpdateService.CheckForUpdatesAsync();
            if (update != null)
            {
                await _updateFlow.RunAsync(update);
            }
            else
            {
                _notificationService.Show(L["Updates.UpToDate"], L["Updates.UpToDateMessage"]);
            }
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Updates.CheckFailed"],
                string.Format(L["Updates.CheckFailedMessage"], ex.Message), NotificationType.Error);
        }
    }

    [RelayCommand]
    private async Task BrowseInstallFolderAsync()
    {
        var folder = await _dialogService.ShowFolderPickerAsync("Select Install Folder", InstallFolder);
        if (!string.IsNullOrEmpty(folder))
        {
            InstallFolder = folder;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var shareUrl = ShareUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(shareUrl))
        {
            await _dialogService.ShowMessageAsync(L["Settings.NoConfigTitle"], L["Settings.NoConfigMessage"]);
            return;
        }

        try
        {
            var config = await _webDav.ResolveConfigAsync(new NextcloudConfig(shareUrl, ShareToken?.Trim() ?? ""));
            var games = await _webDav.DownloadMetadataAsync(config);
            var message = games.Length > 0
                ? string.Format(L["Settings.ConnectionOkFound"], games.Length, config.RootFolder.Length > 0 ? config.RootFolder : "/")
                : L["Settings.ConnectionOkEmpty"];
            _notificationService.Show(L["Settings.ConnectionOkTitle"], message);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(L["Settings.ConnectionErrorTitle"],
                string.Format(L["Settings.ConnectionErrorMessage"], ex.Message));
        }
    }
}
