namespace GameLauncher.Tests;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;
using GameLauncher.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class LibraryViewModelTests : IDisposable
{
    private readonly LocalizationService _localization = new();
    private readonly FakeGameService _games = new();
    private readonly FakeDownloadService _downloads = new();
    private readonly NotificationService _notifications = new();
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly LibraryViewModel _vm;

    public LibraryViewModelTests()
    {
        _localization.SetLanguage("en");

        var factory = new GameItemViewModelFactory(
            _games,
            new StubDialog(),
            _notifications,
            new NullScreenshots(),
            new RecordingShell(),
            _localization,
            _dispatcher,
            NullLogger<GameItemViewModel>.Instance);

        _vm = new LibraryViewModel(_games, _downloads, _notifications, factory, _localization, _dispatcher);
    }

    public void Dispose() => _vm.Dispose();

    [Fact]
    public async Task StatusFilter_Installed_HidesUninstalled()
    {
        SeedLibrary();
        await _vm.InitializeAsync();

        Assert.Equal(3, _vm.FilteredGames.Count);

        _vm.StatusFilters.Single(c => c.Filter == LibraryStatusFilter.Installed).SelectCommand.Execute(null);

        Assert.Equal(2, _vm.FilteredGames.Count);
        Assert.All(_vm.FilteredGames, g => Assert.Equal(InstallStatus.Installed, g.Status));
    }

    [Fact]
    public async Task StatusFilter_Updates_KeepsOnlyVersionMismatch()
    {
        SeedLibrary();
        await _vm.InitializeAsync();

        _vm.StatusFilters.Single(c => c.Filter == LibraryStatusFilter.Updates).SelectCommand.Execute(null);

        var only = Assert.Single(_vm.FilteredGames);
        Assert.Equal("old", only.Game.Id);
        Assert.True(only.IsUpdateAvailable);
    }

    [Fact]
    public async Task Sort_LastPlayed_PutsMostRecentFirst()
    {
        SeedLibrary();
        await _vm.InitializeAsync();

        _vm.SortMode = LibrarySortMode.LastPlayed;

        Assert.Equal("fresh", _vm.FilteredGames[0].Game.Id);
        Assert.Equal("old", _vm.FilteredGames[1].Game.Id);
    }

    [Fact]
    public async Task ClearFilters_ResetsStatusSearchAndTag()
    {
        SeedLibrary();
        await _vm.InitializeAsync();

        _vm.SearchText = "no-match-zzz";
        // Debounce would wait 250ms; apply via status chip then clear.
        _vm.StatusFilters.Single(c => c.Filter == LibraryStatusFilter.Available).SelectCommand.Execute(null);
        _vm.ClearFiltersCommand.Execute(null);

        Assert.Equal("", _vm.SearchText);
        Assert.Equal(LibraryStatusFilter.All, _vm.StatusFilters.Single(c => c.IsSelected).Filter);
        Assert.Equal(3, _vm.FilteredGames.Count);
    }

    [Fact]
    public async Task OpenDetails_ThenBack_ClearsSelection()
    {
        SeedLibrary();
        await _vm.InitializeAsync();

        _vm.OpenDetails(_vm.Games[0]);
        Assert.True(_vm.HasSelectedGame);
        Assert.Same(_vm.Games[0], _vm.SelectedGame);

        _vm.CloseDetailsCommand.Execute(null);
        Assert.False(_vm.HasSelectedGame);
        Assert.Null(_vm.SelectedGame);
    }

    private void SeedLibrary()
    {
        var older = DateTime.UtcNow.AddDays(-10);
        var newer = DateTime.UtcNow.AddHours(-1);

        _games.Games.AddRange(
        [
            MakeGame("fresh", "Fresh", "1.0"),
            MakeGame("old", "Old", "2.0"),
            MakeGame("shop", "Shop", "1.0"),
        ]);
        _games.States.AddRange(
        [
            new GameLocalState("fresh", InstallStatus.Installed, "/tmp/fresh", 60, newer, "1.0"),
            new GameLocalState("old", InstallStatus.Installed, "/tmp/old", 10, older, "1.0"),
        ]);
    }

    private static Game MakeGame(string id, string name, string version) => new(
        id, name, version, "A game", ["action"], [], [], $"{id}/manifest.json", 1_000_000);

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class StubDialog : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null)
            => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message, bool isError = false) => Task.CompletedTask;
        public Task<string?> ShowFolderPickerAsync(string title, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<string?> ShowFilePickerAsync(string title, string? initialPath = null) => Task.FromResult<string?>(null);
    }

    private sealed class NullScreenshots : IScreenshotService
    {
        public Task<byte[]?> LoadCoverAsync(Game game, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> LoadAsync(Game game, int index, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }

    private sealed class RecordingShell : IShellService
    {
        public void OpenFolder(string path) { }
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        public event Action<DownloadTask>? OnTaskUpdated
        {
            add { }
            remove { }
        }

        public event Action<DownloadProgress>? OnProgress
        {
            add { }
            remove { }
        }

        public Task DownloadFilesAsync(DownloadTask task, IReadOnlyList<DownloadFileRequest> files, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateInstallStageAsync(string taskId, InstallStage stage) => Task.CompletedTask;
        public Task<IReadOnlyList<DownloadTask>> GetAllTasksAsync() => Task.FromResult<IReadOnlyList<DownloadTask>>([]);
        public Task<DownloadTask?> GetTaskAsync(string taskId) => Task.FromResult<DownloadTask?>(null);
    }

    private sealed class FakeGameService : IGameService
    {
        public List<Game> Games { get; } = [];
        public List<GameLocalState> States { get; } = [];

        public event Action<GameLocalState>? OnGameStateChanged
        {
            add { }
            remove { }
        }

        public event Action<DownloadTask>? OnTaskUpdated
        {
            add { }
            remove { }
        }

        public event Action<DownloadProgress>? OnProgress
        {
            add { }
            remove { }
        }

        public Task InstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseInstallAsync(string gameId) => Task.CompletedTask;
        public Task ResumeInstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelInstallAsync(string gameId) => Task.CompletedTask;
        public Task UninstallAsync(string gameId) => Task.CompletedTask;
        public Task<LaunchResult> LaunchAsync(string gameId) => Task.FromResult(new LaunchResult(true));
        public Task<GameLocalState?> GetLocalStateAsync(string gameId)
            => Task.FromResult(States.FirstOrDefault(s => s.GameId == gameId));
        public Task<IReadOnlyList<GameLocalState>> GetAllLocalStatesAsync()
            => Task.FromResult<IReadOnlyList<GameLocalState>>(States);
        public Task<Game[]> GetAllGamesAsync() => Task.FromResult(Games.ToArray());
        public Task<Game?> GetGameAsync(string gameId)
            => Task.FromResult(Games.FirstOrDefault(g => g.Id == gameId));
        public Task<bool> VerifyInstallAsync(string gameId) => Task.FromResult(true);
        public Task<int> RefreshFromRemoteAsync(CancellationToken ct = default) => Task.FromResult(Games.Count);
    }
}
