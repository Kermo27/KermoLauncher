using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;

namespace GameLauncher.UI.ViewModels;

public partial class DownloadItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private DownloadTask _task;

    [ObservableProperty]
    private double _speedBytesPerSecond;

    public DownloadItemViewModel(DownloadTask task)
    {
        _task = task;
    }

    partial void OnTaskChanged(DownloadTask value)
    {
        OnPropertyChanged(nameof(GameId));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRemove));
    }

    partial void OnSpeedBytesPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(Speed));
    }

    public string GameId => Task.GameId;
    public string StatusText => Task.Status switch
    {
        DownloadStatus.Queued => L["Downloads.Status.Queued"],
        DownloadStatus.Downloading => Task.InstallStage switch
        {
            InstallStage.Preparing => L["Downloads.Status.Preparing"],
            InstallStage.Downloading => string.Format(L["Downloads.Status.Downloading"],
                Task.DownloadedBytes / 1024 / 1024, Task.TotalBytes / 1024 / 1024),
            InstallStage.Verifying => L["Downloads.Status.Verifying"],
            InstallStage.Extracting => L["Downloads.Status.Extracting"],
            InstallStage.Completed => L["Downloads.Status.Installed"],
            InstallStage.Failed => string.Format(L["Downloads.Status.Error"], Task.Error),
            _ => string.Format(L["Downloads.Status.Downloading"],
                Task.DownloadedBytes / 1024 / 1024, Task.TotalBytes / 1024 / 1024)
        },
        DownloadStatus.Paused => L["Downloads.Status.Paused"],
        DownloadStatus.Completed => Task.InstallStage switch
        {
            InstallStage.Verifying => L["Downloads.Status.Verifying"],
            InstallStage.Extracting => L["Downloads.Status.Extracting"],
            InstallStage.Completed => L["Downloads.Status.Installed"],
            InstallStage.Failed => string.Format(L["Downloads.Status.Error"], Task.Error),
            _ => L["Downloads.Status.Done"]
        },
        DownloadStatus.Failed => string.Format(L["Downloads.Status.Error"], Task.Error),
        DownloadStatus.Cancelled => L["Downloads.Status.Cancelled"],
        _ => Task.Status.ToString()
    };

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(StatusText));
    }

    public double Progress => Task.TotalBytes > 0 ? (double)Task.DownloadedBytes / Task.TotalBytes : 0;
    public double Speed => SpeedBytesPerSecond;
    public bool CanPause => Task.Status == DownloadStatus.Downloading;
    public bool CanResume => Task.Status == DownloadStatus.Paused || Task.Status == DownloadStatus.Failed;
    public bool CanCancel => Task.Status == DownloadStatus.Downloading || Task.Status == DownloadStatus.Queued || Task.Status == DownloadStatus.Paused;
    public bool CanRemove => Task.Status == DownloadStatus.Completed || Task.Status == DownloadStatus.Failed || Task.Status == DownloadStatus.Cancelled;
}

public partial class DownloadsViewModel : ViewModelBase
{
    private readonly IDownloadService _downloadService;
    private readonly IGameService _gameService;
    private readonly Dictionary<string, DownloadItemViewModel> _items = [];

    [ObservableProperty]
    private DownloadItemViewModel[] _downloads = [];

    public DownloadsViewModel(IDownloadService downloadService, IGameService gameService)
    {
        _downloadService = downloadService;
        _gameService = gameService;
        _downloadService.OnTaskUpdated += OnTaskUpdated;
        _downloadService.OnProgress += OnProgress;
        _gameService.OnTaskUpdated += OnTaskUpdated;
        _gameService.OnProgress += OnProgress;
        _ = RefreshAsync();
    }

    private void OnTaskUpdated(DownloadTask task)
    {
        _ = RefreshAsync();
    }
    private void OnProgress(DownloadProgress progress)
    {
        if (_items.TryGetValue(progress.TaskId, out var item))
        {
            Dispatcher.UIThread.Post(() =>
            {
                var updatedTask = item.Task with 
                { 
                    DownloadedBytes = progress.BytesReceived,
                    TotalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : item.Task.TotalBytes
                };
                item.Task = updatedTask;
                item.SpeedBytesPerSecond = progress.SpeedBytesPerSecond;
            });
        }
    }

    private async Task RefreshAsync()
    {
        var tasks = (await _downloadService.GetAllTasksAsync())
            .OrderByDescending(d => d.StartedAt)
            .ToArray();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _items.Clear();
            Downloads = tasks.Select(t =>
            {
                var vm = new DownloadItemViewModel(t);
                _items[t.Id] = vm;
                return vm;
            }).ToArray();
        });
    }

    [RelayCommand]
    private async Task PauseAsync(DownloadItemViewModel item)
    {
        if (item.CanPause)
        {
            await _downloadService.PauseAsync(item.Task.Id);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ResumeAsync(DownloadItemViewModel item)
    {
        if (item.CanResume)
        {
            await _downloadService.ResumeAsync(item.Task.Id);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task CancelAsync(DownloadItemViewModel item)
    {
        if (item.CanCancel)
        {
            await _downloadService.CancelAsync(item.Task.Id);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(DownloadItemViewModel item)
    {
        if (item.CanRemove)
        {
            await _downloadService.RemoveAsync(item.Task.Id);
            await RefreshAsync();
        }
    }
}