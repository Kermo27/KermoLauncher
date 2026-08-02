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
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private AppSettings _settings = new();

    [ObservableProperty]
    private string _installFolder = "";

    [ObservableProperty]
    private string _shareUrl = "";

    [ObservableProperty]
    private string _shareToken = "";

    public string[] ThemeValues { get; } = ["Light", "Dark", "System"];
    public string[] LanguageValues { get; } = ["System", "en", "pl"];

    public string[] ThemeOptions =>
        [L["Settings.Theme.Light"], L["Settings.Theme.Dark"], L["Settings.Theme.System"]];

    public string[] LanguageOptions =>
        [L["Settings.Language.System"], L["Settings.Language.English"], L["Settings.Language.Polish"]];

    public int SelectedThemeIndex
    {
        get => Array.IndexOf(ThemeValues, Settings.Theme);
        set
        {
            if (value >= 0 && value < ThemeValues.Length)
            {
                Settings.Theme = ThemeValues[value];
            }
        }
    }

    public int SelectedLanguageIndex
    {
        get => Array.IndexOf(LanguageValues, Settings.Language);
        set
        {
            if (value >= 0 && value < LanguageValues.Length)
            {
                Settings.Language = LanguageValues[value];
            }
        }
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(ThemeOptions));
        OnPropertyChanged(nameof(LanguageOptions));
    }

    partial void OnSettingsChanged(AppSettings value)
    {
        OnPropertyChanged(nameof(SelectedThemeIndex));
        OnPropertyChanged(nameof(SelectedLanguageIndex));
        InstallFolder = value.InstallFolder;
    }

    partial void OnInstallFolderChanged(string value)
    {
        Settings.InstallFolder = value;
    }

    public SettingsViewModel(ILocalDbService db, IWebDavService webDav, IAutoUpdateService autoUpdateService, IDialogService dialogService, INotificationService notificationService)
    {
        _db = db;
        _webDav = webDav;
        _autoUpdateService = autoUpdateService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        Settings = await _db.GetSettingsAsync();
        if (Settings.Nextcloud != null)
        {
            ShareUrl = Settings.Nextcloud.ShareUrl;
            ShareToken = Settings.Nextcloud.ShareToken;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var shareUrl = ShareUrl?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(shareUrl))
        {
            Settings.Nextcloud = new NextcloudConfig(shareUrl, ShareToken?.Trim() ?? "");
        }
        else
        {
            Settings.Nextcloud = null;
        }

        await _db.SaveSettingsAsync(Settings);
        App.ApplyTheme(Settings.Theme);
        L.SetLanguage(Settings.Language);
        _notificationService.Show(L["Settings.SavedTitle"], L["Settings.SavedMessage"]);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _autoUpdateService.CheckForUpdatesAsync();
            if (update != null)
            {
                _notificationService.Show(L["Updates.AvailableTitle"],
                    string.Format(L["Updates.AvailableMessage"], update.Version));
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