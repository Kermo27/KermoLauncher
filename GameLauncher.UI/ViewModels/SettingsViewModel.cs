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
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private AppSettings _settings = new();

    [ObservableProperty]
    private string _downloadFolder = "";

    [ObservableProperty]
    private string _installFolder = "";

    [ObservableProperty]
    private string _shareUrl = "";

    [ObservableProperty]
    private string _shareToken = "";

    public string[] ThemeOptions { get; } = ["Jasny", "Ciemny", "Systemowy"];
    public string[] ThemeValues { get; } = ["Light", "Dark", "System"];

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

    partial void OnSettingsChanged(AppSettings value)
    {
        OnPropertyChanged(nameof(SelectedThemeIndex));
        DownloadFolder = value.DownloadFolder;
        InstallFolder = value.InstallFolder;
    }

    partial void OnDownloadFolderChanged(string value)
    {
        Settings.DownloadFolder = value;
    }

    partial void OnInstallFolderChanged(string value)
    {
        Settings.InstallFolder = value;
    }

    public SettingsViewModel(ILocalDbService db, IWebDavService webDav, IDialogService dialogService, INotificationService notificationService)
    {
        _db = db;
        _webDav = webDav;
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
        _notificationService.Show("Zapisano ustawienia", "Ustawienia zostały zapisane.");
    }

    [RelayCommand]
    private async Task BrowseDownloadFolderAsync()
    {
        var folder = await _dialogService.ShowFolderPickerAsync("Select Download Folder", DownloadFolder);
        if (!string.IsNullOrEmpty(folder))
        {
            DownloadFolder = folder;
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
            await _dialogService.ShowMessageAsync("Brak konfiguracji",
                "Wklej link udostępniania Nextcloud, zanim przetestujesz połączenie.");
            return;
        }

        try
        {
            var config = await _webDav.ResolveConfigAsync(new NextcloudConfig(shareUrl, ShareToken?.Trim() ?? ""));
            var games = await _webDav.DownloadMetadataAsync(config);
            var baseFolder = config.RootFolder.Length > 0 ? $" w katalogu {config.RootFolder}/" : " w katalogu głównym";
            _notificationService.Show("Połączenie OK",
                games.Length > 0
                    ? $"Znaleziono {games.Length} gier{baseFolder} udostępnienia."
                    : $"Połączenie działa, ale katalog jest pusty (metadata.json{baseFolder}).");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Błąd połączenia",
                $"Nie udało się pobrać metadata.json: {ex.Message}");
        }
    }
}