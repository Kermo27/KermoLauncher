using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;
using GameLauncher.UI.Shared.ViewModels;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI.ViewModels;

/// <summary>
/// Builds game cards with their full set of dependencies, which keeps the commands on the item
/// instead of the list view so XAML never has to climb the tree to a parent DataContext.
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
    private readonly IShellService _shell;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<GameItemViewModel> _logger;

    public GameItemViewModelFactory(
        IGameService gameService,
        IDialogService dialogService,
        INotificationService notificationService,
        IScreenshotService screenshots,
        IShellService shell,
        ILocalizationService localization,
        IUiDispatcher dispatcher,
        ILogger<GameItemViewModel> logger)
    {
        _gameService = gameService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _screenshots = screenshots;
        _shell = shell;
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
        _shell,
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
    private readonly IShellService _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<GameItemViewModel> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _coverLoad;
    private CancellationTokenSource? _galleryCts;
    private Task? _galleryLoad;

    public Game Game { get; }

    public ObservableCollection<ScreenshotItemViewModel> Gallery { get; } = [];

    [ObservableProperty]
    private GameLocalState? _localState;

    [ObservableProperty]
    private Bitmap? _coverImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVerify))]
    private bool _isVerifying;

    public GameItemViewModel(
        Game game,
        GameLocalState? localState,
        IGameService gameService,
        IDialogService dialogService,
        INotificationService notificationService,
        IScreenshotService screenshots,
        IShellService shell,
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
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Starts the cover download. The task is kept so its exceptions cannot go unobserved.</summary>
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

            // The bitmap is created per card, so each card can safely dispose its own.
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
            // The card left the list.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cover load failed for {GameId}", Game.Id);
        }
    }

    /// <summary>Loads every screenshot. Called when details open; <see cref="ClearGallery"/> frees them.</summary>
    public void BeginLoadGallery()
    {
        if (_galleryLoad != null || Game.ScreenshotUrls.Length == 0) return;
        _galleryCts = new CancellationTokenSource();
        _galleryLoad = LoadGalleryAsync(_galleryCts.Token);
    }

    public void ClearGallery()
    {
        _galleryCts?.Cancel();
        _galleryCts?.Dispose();
        _galleryCts = null;
        _galleryLoad = null;

        foreach (var shot in Gallery)
        {
            shot.Dispose();
        }
        Gallery.Clear();
        OnPropertyChanged(nameof(HasGallery));
    }

    private async Task LoadGalleryAsync(CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < Game.ScreenshotUrls.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await _screenshots.LoadAsync(Game, i, ct);
                if (bytes == null) continue;

                using var ms = new MemoryStream(bytes);
                var bitmap = new Bitmap(ms);

                await _dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        bitmap.Dispose();
                        return;
                    }
                    Gallery.Add(new ScreenshotItemViewModel(bitmap));
                    OnPropertyChanged(nameof(HasGallery));
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Details closed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gallery load failed for {GameId}", Game.Id);
        }
    }

    public bool HasCover => CoverImage != null;

    public bool HasGallery => Gallery.Count > 0;

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
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(HasLastPlayed));
        OnPropertyChanged(nameof(CanCancelDownload));
        OnPropertyChanged(nameof(CanPauseDownload));
        OnPropertyChanged(nameof(CanResumeDownload));
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(IsStatusInstalled));
        OnPropertyChanged(nameof(IsStatusBusy));
        OnPropertyChanged(nameof(IsStatusFailed));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ProgressText));

        if (value?.Status == InstallStatus.Paused)
            MarkPaused();
        else if (value?.Status is not (InstallStatus.Downloading or InstallStatus.Installing))
            ClearProgress();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _progressDetail = "";

    [ObservableProperty]
    private InstallStage? _activeInstallStage;

    [ObservableProperty]
    private double _progressPercent;

    /// <summary>Set while a phase reports no measurable byte progress (preparing, verifying, extracting).</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    public InstallStatus Status => LocalState?.Status ?? InstallStatus.NotInstalled;

    public bool CanCancelDownload => Status is InstallStatus.Downloading or InstallStatus.Paused;

    public bool CanPauseDownload => Status == InstallStatus.Downloading;

    public bool CanResumeDownload => Status == InstallStatus.Paused;

    public bool CanInstall => Status is not (InstallStatus.Installed or InstallStatus.Downloading
        or InstallStatus.Installing or InstallStatus.Paused);

    public bool CanUninstall => Status == InstallStatus.Installed;

    public bool CanLaunch => Status == InstallStatus.Installed;

    public bool CanVerify => Status is InstallStatus.Installed or InstallStatus.Failed
        && LocalState?.InstalledManifest != null
        && LocalState.InstalledPath != null;

    public bool CanOpenFolder => !string.IsNullOrEmpty(LocalState?.InstalledPath);

    public bool IsUpdateAvailable => Status == InstallStatus.Installed &&
                                     LocalState!.InstalledVersion != null &&
                                     LocalState.InstalledVersion != Game.Version;

    public bool CanUpdate => IsUpdateAvailable && !IsBusy;

    // Classes for the status pill; the colours live in the theme, not in converters.
    public bool IsStatusInstalled => Status == InstallStatus.Installed;
    public bool IsStatusBusy => Status is InstallStatus.Downloading or InstallStatus.Installing;
    public bool IsStatusFailed => Status == InstallStatus.Failed;

    /// <summary>
    /// Short label for the pill on the cover. Percentages and speed go to <see cref="ProgressText"/>,
    /// because a long string over the artwork was unreadable.
    /// </summary>
    public string StatusText => Status switch
    {
        InstallStatus.NotInstalled => L["Library.Status.NotInstalled"],
        InstallStatus.Downloading => L["Library.Status.Downloading"],
        InstallStatus.Installing => L["Library.Status.Installing"],
        InstallStatus.Installed => L["Library.Status.Installed"],
        InstallStatus.Failed => L["Library.Status.Failed"],
        InstallStatus.Paused => L["Library.Status.Paused"],
        _ => L["Library.Status.Unknown"]
    };

    /// <summary>The progress row replaces the tag row while a game is being fetched or installed.</summary>
    public bool ShowProgress => Status is InstallStatus.Downloading or InstallStatus.Installing
        or InstallStatus.Paused;

    public string ProgressText => ProgressDetail.Length > 0 ? ProgressDetail : StatusText;

    /// <summary>Stage label from DownloadTask (Preparing / Verifying / Extracting…).</summary>
    public void ApplyInstallStage(InstallStage stage)
    {
        var previousStage = ActiveInstallStage;
        ActiveInstallStage = stage;
        if (stage is InstallStage.Preparing or InstallStage.Verifying or InstallStage.Extracting)
        {
            IsProgressIndeterminate = true;
            ProgressDetail = stage switch
            {
                InstallStage.Preparing => L["Library.Status.Preparing"],
                InstallStage.Verifying => L["Library.Status.Verifying"],
                InstallStage.Extracting => L["Library.Status.Extracting"],
                _ => ProgressDetail
            };
        }
        else if (stage == InstallStage.Downloading && previousStage != InstallStage.Downloading)
        {
            // A task update repeats the stage far more often than bytes arrive, so the percentage may
            // only give way to the plain label when the download is genuinely starting.
            IsProgressIndeterminate = true;
            ProgressDetail = "";
        }
        else if (stage is InstallStage.Completed or InstallStage.Failed)
        {
            ClearProgress();
        }
    }

    /// <summary>Byte progress while files are downloading.</summary>
    public void ApplyByteProgress(long bytesReceived, long totalBytes, double speedBytesPerSecond)
    {
        if (Status is not (InstallStatus.Downloading or InstallStatus.Installing))
            return;

        // Prefer stage labels for non-download phases.
        if (ActiveInstallStage is InstallStage.Preparing or InstallStage.Verifying or InstallStage.Extracting)
            return;

        var speed = FormatBytes(speedBytesPerSecond);
        if (totalBytes > 0)
        {
            ProgressPercent = Math.Clamp(100.0 * bytesReceived / totalBytes, 0, 100);
            IsProgressIndeterminate = false;
            ProgressDetail = string.Format(L["Library.Status.DownloadingProgress"],
                ProgressPercent, FormatBytes(bytesReceived), FormatBytes(totalBytes), speed);
        }
        else
        {
            ProgressPercent = 0;
            IsProgressIndeterminate = true;
            ProgressDetail = string.Format(L["Library.Status.DownloadingSpeed"], speed);
        }
    }

    public void ClearProgress()
    {
        ProgressDetail = "";
        ActiveInstallStage = null;
        ProgressPercent = 0;
        IsProgressIndeterminate = false;
    }

    /// <summary>Keeps the bar where the transfer stopped; the last speed is dropped because it is stale.</summary>
    private void MarkPaused()
    {
        ActiveInstallStage = null;
        IsProgressIndeterminate = false;
        ProgressDetail = ProgressPercent > 0
            ? string.Format(L["Library.Status.PausedProgress"], ProgressPercent)
            : "";
    }

    private static string FormatBytes(double bytes)
    {
        if (double.IsNaN(bytes) || double.IsInfinity(bytes) || bytes < 0)
            return "—";
        if (bytes < 1024)
            return $"{bytes:0} B";

        string[] units = ["B", "KB", "MB", "GB"];
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value >= 100
            ? $"{value:0} {units[unit]}"
            : value >= 10
                ? $"{value:0.0} {units[unit]}"
                : $"{value:0.00} {units[unit]}";
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(PlayTimeText));
        OnPropertyChanged(nameof(HasPlayTime));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(HasLastPlayed));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(CanVerify));
    }

    public bool HasPlayTime => PlayDuration.Length > 0;

    public string LastPlayedText
    {
        get
        {
            if (LocalState?.LastPlayed is not { } at) return "";
            return at.ToLocalTime().ToString("g");
        }
    }

    public bool HasLastPlayed => LastPlayedText.Length > 0;

    private string PlayDuration
    {
        get
        {
            var seconds = LocalState?.PlayTimeSeconds ?? 0;
            if (seconds <= 0) return "";

            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
                : ts.TotalMinutes >= 1
                    ? $"{ts.Minutes}m"
                    : $"{ts.Seconds}s";
        }
    }

    public string PlayTimeText
    {
        get
        {
            var duration = PlayDuration;
            return duration.Length == 0 ? "" : string.Format(L["Library.Played"], duration);
        }
    }

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
            // Cancelled by the user; CancelDownload raises its own notification.
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
            // Cancelled by the user.
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
    private async Task PauseDownloadAsync()
    {
        if (!CanPauseDownload) return;

        try
        {
            await _gameService.PauseInstallAsync(Game.Id);
            _notificationService.Show(L["Library.DownloadPausedTitle"],
                string.Format(L["Library.DownloadPausedMessage"], Name));
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.InstallErrorTitle"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task ResumeDownloadAsync()
    {
        if (!CanResumeDownload) return;

        try
        {
            _notificationService.Show(L["Library.DownloadResumedTitle"],
                string.Format(L["Library.DownloadResumedMessage"], Name));
            await _gameService.ResumeInstallAsync(Game);
            _notificationService.Show(L["Library.InstallDoneTitle"],
                string.Format(L["Library.InstallDoneMessage"], Name));
        }
        catch (OperationCanceledException)
        {
            // Paused or cancelled again; those commands raise their own notifications.
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.InstallErrorTitle"],
                string.Format(L["Library.InstallErrorMessage"], Name, ex.Message));
        }
    }

    [RelayCommand]
    private async Task CancelDownloadAsync()
    {
        if (!CanCancelDownload) return;

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

    [RelayCommand]
    private async Task VerifyInstallAsync()
    {
        if (!CanVerify) return;

        IsVerifying = true;
        try
        {
            var ok = await _gameService.VerifyInstallAsync(Game.Id);
            if (ok)
            {
                _notificationService.Show(L["Library.VerifyOkTitle"],
                    string.Format(L["Library.VerifyOkMessage"], Name));
            }
            else
            {
                _notificationService.Show(L["Library.VerifyFailedTitle"],
                    string.Format(L["Library.VerifyFailedMessage"], Name),
                    NotificationType.Warning);
            }
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.VerifyErrorTitle"], ex.Message, NotificationType.Error);
        }
        finally
        {
            IsVerifying = false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var path = LocalState?.InstalledPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            _notificationService.Show(L["Library.OpenFolderErrorTitle"],
                string.Format(L["Library.OpenFolderMissing"], Name),
                NotificationType.Warning);
            return;
        }

        try
        {
            _shell.OpenFolder(path);
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.OpenFolderErrorTitle"],
                string.Format(L["Library.OpenFolderErrorMessage"], Name, ex.Message),
                NotificationType.Error);
        }
    }

    protected override void DisposeCore()
    {
        _cts.Cancel();
        _cts.Dispose();
        ClearGallery();
        CoverImage?.Dispose();
        CoverImage = null;
    }
}

public enum LibrarySortMode
{
    Name,
    PlayTime,
    Size,
    LastPlayed
}

public enum LibraryStatusFilter
{
    All,
    Installed,
    Updates,
    Available
}

/// <summary>
/// A screenshot bitmap owned by the details gallery, so it can be disposed when the panel closes.
/// </summary>
public sealed class ScreenshotItemViewModel : IDisposable
{
    public Bitmap Image { get; }

    public ScreenshotItemViewModel(Bitmap image) => Image = image;

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// A tag filter chip. Selection is item state, so XAML binds it to a style class instead of
/// recomputing a colour through a converter on every change.
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

/// <summary>
/// Install-state chip next to the tag row. Same style as tags; the label is localized.
/// </summary>
public partial class StatusFilterViewModel : ObservableObject
{
    private readonly Action<LibraryStatusFilter> _onSelected;

    public LibraryStatusFilter Filter { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _isSelected;

    public StatusFilterViewModel(
        LibraryStatusFilter filter,
        string label,
        bool isSelected,
        Action<LibraryStatusFilter> onSelected)
    {
        Filter = filter;
        _label = label;
        _isSelected = isSelected;
        _onSelected = onSelected;
    }

    [RelayCommand]
    private void Select() => _onSelected(Filter);
}
