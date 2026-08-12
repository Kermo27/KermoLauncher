using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI.ViewModels;

/// <summary>
/// Tworzy karty gier z pełnym zestawem zależności — dzięki temu komendy siedzą na elemencie,
/// a nie na widoku listy, i XAML nie musi się wspinać po drzewie do DataContextu rodzica.
/// </summary>
public interface IGameItemViewModelFactory
{
    GameItemViewModel Create(Game game, GameLocalState? localState);
}

public sealed class GameItemViewModelFactory : IGameItemViewModelFactory
{
    private readonly IGameService _gameService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IScreenshotService _screenshots;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<GameItemViewModel> _logger;

    public GameItemViewModelFactory(
        IGameService gameService,
        IDialogService dialogService,
        INotificationService notificationService,
        IScreenshotService screenshots,
        ILocalizationService localization,
        IUiDispatcher dispatcher,
        ILogger<GameItemViewModel> logger)
    {
        _gameService = gameService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _screenshots = screenshots;
        _localization = localization;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public GameItemViewModel Create(Game game, GameLocalState? localState) => new(
        game,
        localState,
        _gameService,
        _dialogService,
        _notificationService,
        _screenshots,
        _localization,
        _dispatcher,
        _logger);
}

public partial class GameItemViewModel : ViewModelBase
{
    private readonly IGameService _gameService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IScreenshotService _screenshots;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<GameItemViewModel> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _coverLoad;

    public Game Game { get; }

    [ObservableProperty]
    private GameLocalState? _localState;

    [ObservableProperty]
    private Bitmap? _coverImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private bool _isBusy;

    public GameItemViewModel(
        Game game,
        GameLocalState? localState,
        IGameService gameService,
        IDialogService dialogService,
        INotificationService notificationService,
        IScreenshotService screenshots,
        ILocalizationService localization,
        IUiDispatcher dispatcher,
        ILogger<GameItemViewModel> logger)
        : base(localization)
    {
        Game = game;
        _localState = localState;
        _gameService = gameService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _screenshots = screenshots;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Startuje pobranie okładki. Zadanie jest trzymane, żeby wyjątki nie ginęły.</summary>
    public void BeginLoadCover()
    {
        if (_coverLoad != null || Game.ScreenshotUrls.Length == 0) return;
        _coverLoad = LoadCoverAsync();
    }

    private async Task LoadCoverAsync()
    {
        try
        {
            var bytes = await _screenshots.LoadCoverAsync(Game, _cts.Token);
            if (bytes == null || _cts.IsCancellationRequested) return;

            // Bitmapa powstaje per karta, więc każda karta może ją bezpiecznie zwolnić.
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);

            _dispatcher.Post(() =>
            {
                if (_cts.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }
                CoverImage?.Dispose();
                CoverImage = bitmap;
                OnPropertyChanged(nameof(HasCover));
            });
        }
        catch (OperationCanceledException)
        {
            // Karta zniknęła z listy.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cover load failed for {GameId}", Game.Id);
        }
    }

    public bool HasCover => CoverImage != null;

    partial void OnLocalStateChanged(GameLocalState? value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlayTimeText));
        OnPropertyChanged(nameof(HasPlayTime));
        OnPropertyChanged(nameof(CanCancelDownload));
        OnPropertyChanged(nameof(IsStatusInstalled));
        OnPropertyChanged(nameof(IsStatusBusy));
        OnPropertyChanged(nameof(IsStatusFailed));
    }

    public InstallStatus Status => LocalState?.Status ?? InstallStatus.NotInstalled;

    public bool CanCancelDownload => Status == InstallStatus.Downloading;

    public bool CanInstall => Status is not (InstallStatus.Installed or InstallStatus.Downloading or InstallStatus.Installing);

    public bool CanUninstall => Status == InstallStatus.Installed;

    public bool CanLaunch => Status == InstallStatus.Installed;

    public bool IsUpdateAvailable => Status == InstallStatus.Installed &&
                                     LocalState!.InstalledVersion != null &&
                                     LocalState.InstalledVersion != Game.Version;

    public bool CanUpdate => IsUpdateAvailable && !IsBusy;

    // Klasy dla plakietki statusu — kolory żyją w motywie, nie w konwerterach.
    public bool IsStatusInstalled => Status == InstallStatus.Installed;
    public bool IsStatusBusy => Status is InstallStatus.Downloading or InstallStatus.Installing;
    public bool IsStatusFailed => Status == InstallStatus.Failed;

    public string StatusText => Status switch
    {
        InstallStatus.NotInstalled => L["Library.Status.NotInstalled"],
        InstallStatus.Downloading => L["Library.Status.Downloading"],
        InstallStatus.Installing => L["Library.Status.Installing"],
        // O dostępnej aktualizacji informuje osobna plakietka, więc pigułka statusu
        // trzyma się stanu instalacji — inaczej ten sam napis pojawia się dwa razy.
        InstallStatus.Installed => L["Library.Status.Installed"],
        InstallStatus.Failed => L["Library.Status.Failed"],
        _ => L["Library.Status.Unknown"]
    };

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlayTimeText));
        OnPropertyChanged(nameof(HasPlayTime));
        OnPropertyChanged(nameof(DescriptionText));
    }

    public string PlayTimeText
    {
        get
        {
            var seconds = LocalState?.PlayTimeSeconds ?? 0;
            if (seconds <= 0) return "";

            var ts = TimeSpan.FromSeconds(seconds);
            var duration = ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
                : ts.TotalMinutes >= 1
                    ? $"{ts.Minutes}m"
                    : $"{ts.Seconds}s";
            return string.Format(L["Library.Played"], duration);
        }
    }

    public bool HasPlayTime => PlayTimeText.Length > 0;

    public string SizeText => Game.SizeBytes > 0
        ? $"{Game.SizeBytes / (1024d * 1024d * 1024d):F1} GB"
        : "";

    public bool HasSizeText => SizeText.Length > 0;

    public string Name => Game.Name;
    public string Version => Game.Version;
    public string[] Tags => Game.Tags;
    public string[] ScreenshotUrls => Game.ScreenshotUrls;
    public string Description => Game.Description;

    public string DescriptionText => string.IsNullOrWhiteSpace(Game.Description)
        ? L["Library.NoDescription"]
        : Game.Description;

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Status == InstallStatus.Installed)
        {
            await _dialogService.ShowMessageAsync(L["Library.AlreadyInstalledTitle"],
                string.Format(L["Library.AlreadyInstalledMessage"], Name));
            return;
        }

        if (Status is InstallStatus.Downloading or InstallStatus.Installing)
        {
            await _dialogService.ShowMessageAsync(L["Library.InProgressTitle"],
                string.Format(L["Library.InProgressMessage"], Name));
            return;
        }

        try
        {
            _notificationService.Show(L["Library.InstallStartedTitle"],
                string.Format(L["Library.InstallStartedMessage"], Name));
            await _gameService.InstallAsync(Game);
            _notificationService.Show(L["Library.InstallDoneTitle"],
                string.Format(L["Library.InstallDoneMessage"], Name));
        }
        catch (OperationCanceledException)
        {
            // Anulowane przez użytkownika — osobne powiadomienie leci z CancelDownload.
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.InstallErrorTitle"],
                string.Format(L["Library.InstallErrorMessage"], Name, ex.Message));
        }
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _notificationService.Show(L["Library.UpdateStartedTitle"],
                string.Format(L["Library.UpdateStartedMessage"], Name));
            await _gameService.UpdateAsync(Game);
            _notificationService.Show(L["Library.UpdateDoneTitle"],
                string.Format(L["Library.UpdateDoneMessage"], Name, Game.Version));
        }
        catch (OperationCanceledException)
        {
            // Anulowane przez użytkownika.
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.UpdateErrorTitle"],
                string.Format(L["Library.InstallErrorMessage"], Name, ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CancelDownloadAsync()
    {
        if (Status != InstallStatus.Downloading) return;

        try
        {
            await _gameService.CancelInstallAsync(Game.Id);
            _notificationService.Show(L["Library.DownloadCancelledTitle"],
                string.Format(L["Library.DownloadCancelledMessage"], Name));
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.InstallErrorTitle"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(L["Library.UninstallTitle"],
            string.Format(L["Library.UninstallConfirm"], Name));
        if (!confirmed) return;

        try
        {
            await _gameService.UninstallAsync(Game.Id);
            _notificationService.Show(L["Library.UninstalledTitle"],
                string.Format(L["Library.UninstalledMessage"], Name));
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.UninstallErrorTitle"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (Status != InstallStatus.Installed)
        {
            await _dialogService.ShowMessageAsync(L["Library.NotInstalledTitle"],
                string.Format(L["Library.NotInstalledMessage"], Name));
            return;
        }

        var result = await _gameService.LaunchAsync(Game.Id);
        if (!result.Success)
        {
            _notificationService.Show(L["Library.LaunchErrorTitle"], result.Error ?? L["Library.UnknownError"]);
        }
    }

    protected override void DisposeCore()
    {
        _cts.Cancel();
        _cts.Dispose();
        CoverImage?.Dispose();
        CoverImage = null;
    }
}

public enum LibrarySortMode
{
    Name,
    PlayTime,
    Size
}

/// <summary>
/// Chip filtra tagów. Zaznaczenie jest stanem elementu, więc XAML podpina je klasą stylu
/// zamiast liczyć kolor konwerterem na każdą zmianę.
/// </summary>
public partial class TagFilterViewModel : ObservableObject
{
    private readonly Action<string> _onSelected;

    public string Tag { get; }

    [ObservableProperty]
    private bool _isSelected;

    public TagFilterViewModel(string tag, bool isSelected, Action<string> onSelected)
    {
        Tag = tag;
        _isSelected = isSelected;
        _onSelected = onSelected;
    }

    [RelayCommand]
    private void Select() => _onSelected(Tag);
}
