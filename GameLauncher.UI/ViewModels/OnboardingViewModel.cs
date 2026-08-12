using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.Core.Utils;
using GameLauncher.UI.Services;

namespace GameLauncher.UI.ViewModels;

public partial class OnboardingViewModel : ViewModelBase
{
    public const string ShareUrlEnvironmentVariable = "KERMO_NEXTCLOUD_SHARE_URL";

    private readonly ILocalDbService _db;
    private readonly IWebDavService _webDav;
    private readonly IDialogService _dialogService;
    private readonly IGameService _gameService;

    /// <summary>Set by MainWindowViewModel after construction to avoid a DI cycle.</summary>
    public Func<Task>? Completed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeStep))]
    [NotifyPropertyChangedFor(nameof(IsFolderStep))]
    [NotifyPropertyChangedFor(nameof(IsSourceStep))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText))]
    private int _step;

    [ObservableProperty]
    private int _selectedThemeIndex = 2;

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueFromFolder))]
    private string _installFolder = "";

    [ObservableProperty]
    private string? _folderStatus;

    [ObservableProperty]
    private bool _folderIsValid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueFromSource))]
    private string _shareUrl = "";

    [ObservableProperty]
    private string _shareToken = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueFromSource))]
    [NotifyPropertyChangedFor(nameof(HasConnectionStatus))]
    private string? _connectionStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueFromSource))]
    private bool _connectionOk;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasConnectionStatus => !string.IsNullOrWhiteSpace(ConnectionStatus);
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public string[] ThemeValues { get; } = ["Light", "Dark", "System"];
    public string[] LanguageValues { get; } = ["System", "en", "pl"];

    public string[] ThemeOptions =>
        [L["Settings.Theme.Light"], L["Settings.Theme.Dark"], L["Settings.Theme.System"]];

    public string[] LanguageOptions =>
        [L["Settings.Language.System"], L["Settings.Language.English"], L["Settings.Language.Polish"]];

    public bool IsWelcomeStep => Step == 0;
    public bool IsFolderStep => Step == 1;
    public bool IsSourceStep => Step == 2;
    public bool CanGoBack => Step > 0 && !IsBusy;

    public bool CanContinueFromFolder => FolderIsValid && !string.IsNullOrWhiteSpace(InstallFolder);
    public bool CanContinueFromSource => ConnectionOk && !string.IsNullOrWhiteSpace(ShareUrl);

    public string PrimaryButtonText => Step switch
    {
        0 => L["Onboarding.Next"],
        1 => L["Onboarding.Next"],
        _ => L["Onboarding.Finish"]
    };

    public OnboardingViewModel(
        ILocalDbService db,
        IWebDavService webDav,
        IDialogService dialogService,
        IGameService gameService,
        ILocalizationService localization)
        : base(localization)
    {
        _db = db;
        _webDav = webDav;
        _dialogService = dialogService;
        _gameService = gameService;
    }

    public void Initialize()
    {
        InstallFolder = Core.Utils.InstallFolder.DefaultPath;
        ValidateFolder();

        var fromEnv = Environment.GetEnvironmentVariable(ShareUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            ShareUrl = fromEnv.Trim();
        }
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(ThemeOptions));
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        App.ApplyTheme(ThemeValues[Math.Clamp(value, 0, ThemeValues.Length - 1)]);
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        L.SetLanguage(LanguageValues[Math.Clamp(value, 0, LanguageValues.Length - 1)]);
    }

    partial void OnInstallFolderChanged(string value) => ValidateFolder();

    private void ValidateFolder()
    {
        if (!Core.Utils.InstallFolder.TryValidate(InstallFolder, out var error, out var freeBytes))
        {
            FolderIsValid = false;
            FolderStatus = error == "empty"
                ? L["Onboarding.FolderEmpty"]
                : string.Format(L["Onboarding.FolderInvalid"], error);
            return;
        }

        FolderIsValid = true;
        FolderStatus = string.Format(L["Onboarding.FolderOk"], FormatBytes(freeBytes));
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var folder = await _dialogService.ShowFolderPickerAsync(L["Onboarding.BrowseTitle"], InstallFolder);
        if (!string.IsNullOrEmpty(folder))
        {
            InstallFolder = folder;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        Error = null;
        ConnectionOk = false;
        ConnectionStatus = null;

        var shareUrl = ShareUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(shareUrl))
        {
            ConnectionStatus = L["Settings.NoConfigMessage"];
            return;
        }

        IsBusy = true;
        try
        {
            var config = await _webDav.ResolveConfigAsync(new NextcloudConfig(shareUrl, ShareToken?.Trim() ?? ""));
            var games = await _webDav.DownloadMetadataAsync(config);
            ConnectionOk = true;
            ConnectionStatus = games.Length > 0
                ? string.Format(L["Settings.ConnectionOkFound"], games.Length,
                    config.RootFolder.Length > 0 ? config.RootFolder : "/")
                : L["Settings.ConnectionOkEmpty"];
        }
        catch (Exception ex)
        {
            ConnectionOk = false;
            ConnectionStatus = string.Format(L["Settings.ConnectionErrorMessage"], ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0) Step--;
        Error = null;
    }

    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private async Task PrimaryAsync()
    {
        Error = null;
        if (Step < 2)
        {
            if (Step == 0)
            {
                App.ApplyTheme(ThemeValues[Math.Clamp(SelectedThemeIndex, 0, ThemeValues.Length - 1)]);
                L.SetLanguage(LanguageValues[Math.Clamp(SelectedLanguageIndex, 0, LanguageValues.Length - 1)]);
            }
            else if (Step == 1)
            {
                ValidateFolder();
                if (!CanContinueFromFolder) return;
            }

            Step++;
            return;
        }

        await FinishAsync();
    }

    private bool CanPrimary() => Step switch
    {
        0 => !IsBusy,
        1 => CanContinueFromFolder && !IsBusy,
        2 => CanContinueFromSource && !IsBusy,
        _ => false
    };

    partial void OnStepChanged(int value) => PrimaryCommand.NotifyCanExecuteChanged();
    partial void OnFolderIsValidChanged(bool value) => PrimaryCommand.NotifyCanExecuteChanged();
    partial void OnConnectionOkChanged(bool value) => PrimaryCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => PrimaryCommand.NotifyCanExecuteChanged();
    partial void OnShareUrlChanged(string value)
    {
        ConnectionOk = false;
        ConnectionStatus = null;
        PrimaryCommand.NotifyCanExecuteChanged();
    }

    private async Task FinishAsync()
    {
        if (!CanContinueFromSource) return;

        IsBusy = true;
        try
        {
            var shareUrl = ShareUrl.Trim();
            var config = await _webDav.ResolveConfigAsync(
                new NextcloudConfig(shareUrl, ShareToken?.Trim() ?? ""));

            var existing = await _db.GetSettingsAsync();
            var settings = new AppSettings
            {
                InstallFolder = InstallFolder.Trim(),
                MaxParallelDownloads = existing.MaxParallelDownloads > 0 ? existing.MaxParallelDownloads : 2,
                AutoUpdate = existing.AutoUpdate,
                Theme = ThemeValues[Math.Clamp(SelectedThemeIndex, 0, ThemeValues.Length - 1)],
                Language = LanguageValues[Math.Clamp(SelectedLanguageIndex, 0, LanguageValues.Length - 1)],
                Nextcloud = config,
                OnboardingCompleted = true
            };

            await _db.SaveSettingsAsync(settings);
            App.ApplyTheme(settings.Theme);
            L.SetLanguage(settings.Language);

            try
            {
                await _gameService.RefreshFromRemoteAsync();
            }
            catch (Exception ex)
            {
                // Setup is saved; the library can still open and show a sync error.
                Error = string.Format(L["Onboarding.SyncFailed"], ex.Message);
            }

            if (Completed != null)
            {
                await Completed();
            }
        }
        catch (Exception ex)
        {
            Error = string.Format(L["Onboarding.FinishFailed"], ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
