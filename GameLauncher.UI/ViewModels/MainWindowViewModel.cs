using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;

namespace GameLauncher.UI.ViewModels;

public partial class ToastItemViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private readonly CancellationTokenSource _cts = new();

    public string Title { get; }
    public string Message { get; }
    public NotificationType Type { get; }

    public ToastItemViewModel(Notification notification, MainWindowViewModel owner)
    {
        _owner = owner;
        Title = notification.Title;
        Message = notification.Message;
        Type = notification.Type;
        _ = DismissAfterDelayAsync();
    }

    private async Task DismissAfterDelayAsync()
    {
        await Task.Delay(5000);
        if (!_cts.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _owner.RemoveToast(this));
        }
    }

    [RelayCommand]
    private void Close() => _owner.RemoveToast(this);

    public void Detach() => _cts.Cancel();
}

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly INotificationService _notificationService;
    private readonly IDownloadService _downloadService;
    private readonly IAutoUpdateService _autoUpdateService;
    private readonly ILocalDbService _db;

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private string _windowTitle = "KermoLauncher";

    [ObservableProperty]
    private int _activeDownloadsCount;

    [ObservableProperty]
    private bool _isLibraryActive;

    [ObservableProperty]
    private bool _isDownloadsActive;

    [ObservableProperty]
    private bool _isSettingsActive;

    public ObservableCollection<ToastItemViewModel> Toasts { get; } = [];

    public string WindowVersion => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public LibraryViewModel LibraryVm { get; }
    public DownloadsViewModel DownloadsVm { get; }
    public SettingsViewModel SettingsVm { get; }

    public MainWindowViewModel(
        LibraryViewModel libraryVm,
        DownloadsViewModel downloadsVm,
        SettingsViewModel settingsVm,
        INotificationService notificationService,
        IDownloadService downloadService,
        IAutoUpdateService autoUpdateService,
        ILocalDbService db)
    {
        _notificationService = notificationService;
        _downloadService = downloadService;
        _autoUpdateService = autoUpdateService;
        _db = db;

        LibraryVm = libraryVm;
        DownloadsVm = downloadsVm;
        SettingsVm = settingsVm;

        CurrentView = LibraryVm;
        IsLibraryActive = true;

        _notificationService.NotificationRaised += OnNotificationRaised;

        _ = LibraryVm.InitializeAsync();

        downloadService.OnTaskUpdated += OnDownloadTaskUpdated;
        _ = CheckForUpdatesAsync();
    }

    private void OnNotificationRaised(Notification notification)
    {
        Dispatcher.UIThread.Post(() =>
        {
            while (Toasts.Count >= 4)
            {
                Toasts[0].Detach();
                Toasts.RemoveAt(0);
            }
            Toasts.Add(new ToastItemViewModel(notification, this));
        });
    }

    public void RemoveToast(ToastItemViewModel toast)
    {
        toast.Detach();
        Toasts.Remove(toast);
    }

    partial void OnCurrentViewChanged(ViewModelBase value)
    {
        IsLibraryActive = value == LibraryVm;
        IsDownloadsActive = value == DownloadsVm;
        IsSettingsActive = value == SettingsVm;
    }

    private void OnDownloadTaskUpdated(DownloadTask task)
    {
        _ = UpdateDownloadCountAsync();
    }

    private async Task UpdateDownloadCountAsync()
    {
        var tasks = await _downloadService.GetAllTasksAsync();
        ActiveDownloadsCount = tasks.Count(t => t.Status == DownloadStatus.Downloading);
    }

    [RelayCommand]
    private void ShowLibrary() => CurrentView = LibraryVm;

    [RelayCommand]
    private void ShowDownloads() => CurrentView = DownloadsVm;

    [RelayCommand]
    private void ShowSettings() => CurrentView = SettingsVm;

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var settings = await _db.GetSettingsAsync();
            if (!settings.AutoUpdate) return;

            var update = await _autoUpdateService.CheckForUpdatesAsync();
            if (update != null)
            {
                _notificationService.Show("Dostępna aktualizacja",
                    $"Nowa wersja {update.Version} launchera jest dostępna na GitHub.");
            }
        }
        catch
        {
            // Ignore update check errors
        }
    }
}
