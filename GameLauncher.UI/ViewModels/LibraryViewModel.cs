using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;

namespace GameLauncher.UI.ViewModels;

public partial class LibraryViewModel : ViewModelBase
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IGameService _gameService;
    private readonly IDownloadService _downloadService;
    private readonly INotificationService _notificationService;
    private readonly IGameItemViewModelFactory _itemFactory;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>Maps download task id → game id for byte-progress events.</summary>
    private readonly Dictionary<string, string> _taskGameIds = new(StringComparer.Ordinal);

    private readonly object[] _skeletons = new object[6];
    private CancellationTokenSource? _searchDebounce;
    private bool _hasRefreshed;

    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    /// <summary>The list view. Recomputed once per filter change, not on every read.</summary>
    public ObservableCollection<GameItemViewModel> FilteredGames { get; } = [];

    public ObservableCollection<TagFilterViewModel> Tags { get; } = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSortIndex))]
    private LibrarySortMode _sortMode = LibrarySortMode.Name;

    private string _selectedTag = AllTag;

    private const string AllTag = "All";

    /// <summary>Empty once loading is done, so the skeletons do not linger in the visual tree.</summary>
    public object[] SkeletonItems => IsLoading ? _skeletons : [];

    public string[] SortOptions => [L["Library.SortName"], L["Library.SortPlayTime"], L["Library.SortSize"]];

    public int SelectedSortIndex
    {
        get => (int)SortMode;
        set
        {
            if (value >= 0 && value < Enum.GetValues<LibrarySortMode>().Length)
            {
                SortMode = (LibrarySortMode)value;
            }
        }
    }

    public int FilteredCount => FilteredGames.Count;

    public string CountText => string.Format(L["Library.CountGames"], FilteredCount);

    public bool HasNoGames => !IsLoading && Games.Count == 0;

    public bool HasNoFilteredResults => !IsLoading && Games.Count > 0 && FilteredGames.Count == 0;

    public LibraryViewModel(
        IGameService gameService,
        IDownloadService downloadService,
        INotificationService notificationService,
        IGameItemViewModelFactory itemFactory,
        ILocalizationService localization,
        IUiDispatcher dispatcher)
        : base(localization)
    {
        _gameService = gameService;
        _downloadService = downloadService;
        _notificationService = notificationService;
        _itemFactory = itemFactory;
        _dispatcher = dispatcher;

        _gameService.OnGameStateChanged += OnGameStateChanged;
        _downloadService.OnTaskUpdated += OnDownloadTaskUpdated;
        _gameService.OnProgress += OnDownloadProgress;
    }

    public Task InitializeAsync() => LoadAsync();

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
                // Syncing the catalog is optional; whatever is stored locally is shown instead.
            }

            var games = await _gameService.GetAllGamesAsync();
            var states = await _gameService.GetAllLocalStatesAsync();
            var stateById = states.ToDictionary(s => s.GameId);

            var tags = games.SelectMany(g => g.Tags).Distinct().OrderBy(t => t).ToList();
            tags.Insert(0, AllTag);

            var loadError = games.Length == 0 && !_hasRefreshed
                ? L["Library.EmptyLoadError"]
                : null;

            await _dispatcher.InvokeAsync(() =>
            {
                ReplaceGames(games.Select(g => _itemFactory.Create(g, stateById.GetValueOrDefault(g.Id))));
                ReplaceTags(tags);
                LoadError = loadError;
                _hasRefreshed = true;
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                LoadError = string.Format(L["Library.LoadError"], ex.Message);
                IsLoading = false;
            });
        }
    }

    private void ReplaceGames(IEnumerable<GameItemViewModel> items)
    {
        foreach (var old in Games)
        {
            old.Dispose();
        }
        Games.Clear();

        foreach (var item in items)
        {
            item.BeginLoadCover();
            Games.Add(item);
        }

        ApplyFilter();
    }

    private void ReplaceTags(IEnumerable<string> tags)
    {
        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(new TagFilterViewModel(tag, tag == _selectedTag, SelectTag));
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<GameItemViewModel> query = Games;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(g =>
                g.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                g.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                g.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        if (_selectedTag != AllTag)
        {
            query = query.Where(g => g.Tags.Contains(_selectedTag));
        }

        query = SortMode switch
        {
            LibrarySortMode.PlayTime => query.OrderByDescending(g => g.LocalState?.PlayTimeSeconds ?? 0),
            LibrarySortMode.Size => query.OrderByDescending(g => g.Game.SizeBytes),
            _ => query.OrderBy(g => g.Name)
        };

        FilteredGames.Clear();
        foreach (var item in query)
        {
            FilteredGames.Add(item);
        }

        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasNoGames));
        OnPropertyChanged(nameof(HasNoFilteredResults));
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
                await _dispatcher.InvokeAsync(() => LoadError = L["Library.SyncNotFound"]);
            }
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() => LoadError = string.Format(L["Library.RefreshError"], ex.Message));
            _notificationService.Show(L["Library.SyncErrorTitle"], ex.Message);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsRefreshing = false);
        }
    }

    private void OnGameStateChanged(GameLocalState state)
    {
        _dispatcher.Post(() =>
        {
            var item = Games.FirstOrDefault(i => i.Game.Id == state.GameId);
            if (item != null)
            {
                item.LocalState = state;
                if (SortMode == LibrarySortMode.PlayTime) ApplyFilter();
                return;
            }

            // The game appeared mid-session, so its metadata is fetched off the UI thread.
            _ = AddGameAsync(state);
        });
    }

    private async Task AddGameAsync(GameLocalState state)
    {
        try
        {
            var game = await _gameService.GetGameAsync(state.GameId);
            if (game == null) return;

            await _dispatcher.InvokeAsync(() =>
            {
                if (Games.Any(i => i.Game.Id == state.GameId)) return;

                var item = _itemFactory.Create(game, state);
                item.BeginLoadCover();
                Games.Add(item);
                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show(L["Library.SyncErrorTitle"], ex.Message, NotificationType.Error);
        }
    }

    private void OnDownloadTaskUpdated(DownloadTask task)
    {
        _dispatcher.Post(() =>
        {
            _taskGameIds[task.Id] = task.GameId;

            var item = Games.FirstOrDefault(i => i.Game.Id == task.GameId);
            if (item?.LocalState == null) return;

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

            if (newStatus is InstallStatus.Downloading or InstallStatus.Installing)
                item.ApplyInstallStage(task.InstallStage);
            else
            {
                item.ClearProgress();
                _taskGameIds.Remove(task.Id);
            }
        });
    }

    private void OnDownloadProgress(DownloadProgress progress)
    {
        _dispatcher.Post(() =>
        {
            if (!_taskGameIds.TryGetValue(progress.TaskId, out var gameId))
                return;

            var item = Games.FirstOrDefault(i => i.Game.Id == gameId);
            item?.ApplyByteProgress(
                progress.BytesReceived,
                progress.TotalBytes,
                progress.SpeedBytesPerSecond);
        });
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(SkeletonItems));
        OnPropertyChanged(nameof(HasNoGames));
        OnPropertyChanged(nameof(HasNoFilteredResults));
    }

    /// <summary>
    /// Filtering on every keystroke recomputed the whole list; a 250 ms delay is enough to keep
    /// typing from freezing the interface on a large catalog.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new CancellationTokenSource();
        var token = _searchDebounce.Token;

        _ = DebounceFilterAsync(token);
    }

    private async Task DebounceFilterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounce, token);
            await _dispatcher.InvokeAsync(ApplyFilter);
        }
        catch (OperationCanceledException)
        {
            // The user typed another character.
        }
    }

    partial void OnSortModeChanged(LibrarySortMode value) => ApplyFilter();

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(SortOptions));
        OnPropertyChanged(nameof(CountText));
    }

    private void SelectTag(string tag)
    {
        if (_selectedTag == tag) return;
        _selectedTag = tag;

        foreach (var chip in Tags)
        {
            chip.IsSelected = chip.Tag == tag;
        }
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectTag(AllTag);
    }

    protected override void DisposeCore()
    {
        _gameService.OnGameStateChanged -= OnGameStateChanged;
        _downloadService.OnTaskUpdated -= OnDownloadTaskUpdated;
        _gameService.OnProgress -= OnDownloadProgress;

        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();

        foreach (var item in Games)
        {
            item.Dispose();
        }
        Games.Clear();
        FilteredGames.Clear();
    }
}
