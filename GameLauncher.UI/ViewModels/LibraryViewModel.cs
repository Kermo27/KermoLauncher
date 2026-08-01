using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;

namespace GameLauncher.UI.ViewModels;

public partial class GameItemViewModel : ViewModelBase
{
    private readonly IScreenshotService _screenshots;

    public Game Game { get; }
    
    [ObservableProperty]
    private GameLocalState? _localState;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _coverImage;

    public GameItemViewModel(Game game, IScreenshotService screenshots, GameLocalState? localState = null)
    {
        Game = game;
        _screenshots = screenshots;
        _localState = localState;
        _ = LoadCoverAsync();
    }

    public bool HasCover => CoverImage != null;

    private async Task LoadCoverAsync()
    {
        if (Game.ScreenshotUrls.Length == 0) return;

        var bmp = await _screenshots.LoadCoverAsync(Game);
        if (bmp != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CoverImage = bmp;
                OnPropertyChanged(nameof(HasCover));
            });
        }
    }

    partial void OnLocalStateChanged(GameLocalState? value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(StatusText));
    }

    public bool CanInstall => LocalState?.Status != InstallStatus.Installed && 
                              LocalState?.Status != InstallStatus.Downloading && 
                              LocalState?.Status != InstallStatus.Installing;

    public bool CanUninstall => LocalState?.Status == InstallStatus.Installed;

    public bool CanLaunch => LocalState?.Status == InstallStatus.Installed;

    public bool IsUpdateAvailable => LocalState?.Status == InstallStatus.Installed &&
                                     LocalState.InstalledVersion != null &&
                                     LocalState.InstalledVersion != Game.Version;

    public bool CanUpdate => IsUpdateAvailable && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private bool _isBusy;

    public string StatusText => (LocalState?.Status ?? InstallStatus.NotInstalled) switch
    {
        InstallStatus.NotInstalled => L["Library.Status.NotInstalled"],
        InstallStatus.Downloading => L["Library.Status.Downloading"],
        InstallStatus.Installing => L["Library.Status.Installing"],
        InstallStatus.Installed => IsUpdateAvailable ? L["Library.Status.UpdateAvailable"] : L["Library.Status.Installed"],
        InstallStatus.Failed => L["Library.Status.Failed"],
        _ => L["Library.Status.Unknown"]
    };

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(StatusText));
    }

    public string SizeText => Game.SizeBytes > 0
        ? $"{Game.SizeBytes / (1024d * 1024d * 1024d):F1} GB"
        : "";

    public string Name => Game.Name;
    public string Version => Game.Version;
    public string[] Tags => Game.Tags;
    public string[] ScreenshotUrls => Game.ScreenshotUrls;
    public string Description => Game.Description;
}

public partial class LibraryViewModel : ViewModelBase
{
    private readonly IGameService _gameService;
    private readonly IDownloadService _downloadService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IScreenshotService _screenshots;

    [ObservableProperty]
    private GameItemViewModel[] _gameItems = [];

    [ObservableProperty]
    private bool _isLoading = true;

    public object[] SkeletonItems { get; } = new object[6]; // 6 skeleton cards

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedTag = "All";

    [ObservableProperty]
    private string[] _availableTags = ["All"];

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string? _loadError;

    private bool _hasRefreshed;

    public LibraryViewModel(
        IGameService gameService,
        IDownloadService downloadService,
        IDialogService dialogService,
        INotificationService notificationService,
        IScreenshotService screenshots)
    {
        _gameService = gameService;
        _downloadService = downloadService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _screenshots = screenshots;

        _gameService.OnGameStateChanged += OnGameStateChanged;
        _downloadService.OnTaskUpdated += OnDownloadTaskUpdated;
    }

    public async Task InitializeAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            try
            {
                await _gameService.RefreshFromRemoteAsync();
            }
            catch
            {
                // Catalog sync is optional - show what we have locally
            }

            var games = await _gameService.GetAllGamesAsync();
            var states = await _gameService.GetAllLocalStatesAsync();
            
            var stateDict = states.ToDictionary(s => s.GameId);
            var items = games.Select(g => new GameItemViewModel(g, _screenshots, stateDict.GetValueOrDefault(g.Id))).ToArray();
            
            var tags = games.SelectMany(g => g.Tags).Distinct().OrderBy(t => t).ToList();
            tags.Insert(0, "All");

            var loadError = games.Length == 0 && !_hasRefreshed
                ? L["Library.EmptyLoadError"]
                : null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GameItems = items;
                AvailableTags = tags.ToArray();
                LoadError = loadError;
                _hasRefreshed = true;
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoadError = string.Format(L["Library.LoadError"], ex.Message);
                IsLoading = false;
            });
        }
    }


    public async Task EnsureLoadedAsync()
    {
        if (!_hasRefreshed)
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        LoadError = null;
        try
        {
            var count = await _gameService.RefreshFromRemoteAsync();
            await LoadAsync();
            _notificationService.Show(L["Library.SyncTitle"],
                count > 0 ? string.Format(L["Library.SyncDone"], count) : L["Library.SyncEmpty"]);
            if (count == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    LoadError = L["Library.SyncNotFound"]);
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadError = string.Format(L["Library.RefreshError"], ex.Message));
            _notificationService.Show(L["Library.SyncErrorTitle"], ex.Message);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsRefreshing = false);
        }
    }

    private void OnGameStateChanged(GameLocalState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = GameItems.FirstOrDefault(i => i.Game.Id == state.GameId);
            if (item != null)
            {
                item.LocalState = state;
            }
            else
            {
                var game = _gameService.GetGameAsync(state.GameId).GetAwaiter().GetResult();
                if (game != null)
                {
                    var newItem = new GameItemViewModel(game, _screenshots, state);
                    GameItems = [.. GameItems, newItem];
                }
            }
        });
    }

    private void OnDownloadTaskUpdated(DownloadTask task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = GameItems.FirstOrDefault(i => i.Game.Id == task.GameId);
            if (item != null && item.LocalState != null)
            {
                var newStatus = task.Status switch
                {
                    DownloadStatus.Downloading => InstallStatus.Downloading,
                    DownloadStatus.Completed => task.InstallStage == InstallStage.Completed
                        ? InstallStatus.Installed
                        : InstallStatus.Installing,
                    DownloadStatus.Failed => InstallStatus.Failed,
                    DownloadStatus.Cancelled => InstallStatus.NotInstalled,
                    _ => item.LocalState.Status
                };
                
                if (newStatus != item.LocalState.Status)
                {
                    item.LocalState = item.LocalState with { Status = newStatus };
                }
            }
        });
    }

    public IEnumerable<GameItemViewModel> FilteredGameItems
    {
        get
        {
            var query = GameItems.AsEnumerable();
            
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var search = SearchText.ToLower();
                query = query.Where(g => g.Name.ToLower().Contains(search) || 
                                         g.Description.ToLower().Contains(search) ||
                                         g.Tags.Any(t => t.ToLower().Contains(search)));
            }
            
            if (SelectedTag != "All")
            {
                query = query.Where(g => g.Tags.Contains(SelectedTag));
            }
            
            return query.OrderBy(g => g.Name);
        }
    }

    public int FilteredCount => FilteredGameItems.Count();

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredGameItems));
        OnPropertyChanged(nameof(FilteredCount));
    }

    partial void OnSelectedTagChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredGameItems));
        OnPropertyChanged(nameof(FilteredCount));
    }

    partial void OnGameItemsChanged(GameItemViewModel[] value)
    {
        OnPropertyChanged(nameof(FilteredGameItems));
        OnPropertyChanged(nameof(FilteredCount));
    }

    [RelayCommand]
    private async Task InstallAsync(GameItemViewModel item)
    {
        if (item.LocalState?.Status == InstallStatus.Installed)
        {
            await _dialogService.ShowMessageAsync(L["Library.AlreadyInstalledTitle"],
                string.Format(L["Library.AlreadyInstalledMessage"], item.Name));
            return;
        }

        if (item.LocalState?.Status == InstallStatus.Downloading || item.LocalState?.Status == InstallStatus.Installing)
        {
            await _dialogService.ShowMessageAsync(L["Library.InProgressTitle"],
                string.Format(L["Library.InProgressMessage"], item.Name));
            return;
        }

        try
        {
            _notificationService.Show(L["Library.InstallStartedTitle"],
                string.Format(L["Library.InstallStartedMessage"], item.Name));
            await _gameService.InstallAsync(item.Game);
            _notificationService.Show(L["Library.InstallDoneTitle"],
                string.Format(L["Library.InstallDoneMessage"], item.Name));
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.InstallErrorTitle"],
                string.Format(L["Library.InstallErrorMessage"], item.Name, ex.Message));
        }
    }

    [RelayCommand]
    private async Task UpdateAsync(GameItemViewModel item)
    {
        if (item.IsBusy) return;
        item.IsBusy = true;
        try
        {
            _notificationService.Show(L["Library.UpdateStartedTitle"],
                string.Format(L["Library.UpdateStartedMessage"], item.Name));
            await _gameService.UpdateAsync(item.Game);
            _notificationService.Show(L["Library.UpdateDoneTitle"],
                string.Format(L["Library.UpdateDoneMessage"], item.Name, item.Game.Version));
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.UpdateErrorTitle"],
                string.Format(L["Library.InstallErrorMessage"], item.Name, ex.Message));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(GameItemViewModel item)
    {
        var confirmed = await _dialogService.ShowConfirmAsync(L["Library.UninstallTitle"],
            string.Format(L["Library.UninstallConfirm"], item.Name));
        if (!confirmed) return;

        try
        {
            await _gameService.UninstallAsync(item.Game.Id);
            _notificationService.Show(L["Library.UninstalledTitle"],
                string.Format(L["Library.UninstalledMessage"], item.Name));
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.UninstallErrorTitle"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task LaunchAsync(GameItemViewModel item)
    {
        if (item.LocalState?.Status != InstallStatus.Installed)
        {
            await _dialogService.ShowMessageAsync(L["Library.NotInstalledTitle"],
                string.Format(L["Library.NotInstalledMessage"], item.Name));
            return;
        }

        var result = await _gameService.LaunchAsync(item.Game.Id);
        if (!result.Success)
        {
            _notificationService.Show(L["Library.LaunchErrorTitle"], result.Error ?? L["Library.UnknownError"]);
        }
    }

    [RelayCommand]
    private async Task ShowDetailsAsync(GameItemViewModel item)
    {
        var size = item.SizeText.Length > 0 ? string.Format(L["Library.DetailsSize"], item.SizeText) : "";
        var tags = item.Tags.Length > 0 ? string.Format(L["Library.DetailsTags"], string.Join(", ", item.Tags)) : "";
        var parts = new[] { item.Description, tags, size }.Where(p => p.Length > 0);
        var details = string.Join("\n\n", parts);
        if (details.Length == 0) details = L["Library.NoDescription"];
        await _dialogService.ShowMessageAsync(string.Format(L["Library.DetailsTitle"], item.Name, item.Version), details);
    }
}