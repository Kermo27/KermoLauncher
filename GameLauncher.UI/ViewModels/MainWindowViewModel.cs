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

    [ObservableProperty]
    private double? _progress;

    public bool IsProgressVisible => Progress != null;

    public ToastItemViewModel(Notification notification, MainWindowViewModel owner, bool autoDismiss = true)
    {
        _owner = owner;
        Title = notification.Title;
        Message = notification.Message;
        Type = notification.Type;
        if (autoDismiss)
        {
            _ = DismissAfterDelayAsync();
        }
    }

    private static int GetDurationMs(NotificationType type) => type switch
    {
        NotificationType.Warning or NotificationType.Error => 10000,
        _ => 6000
    };

    private async Task DismissAfterDelayAsync()
    {
        await Task.Delay(GetDurationMs(Type));
        if (!_cts.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _owner.RemoveToast(this));
        }
    }

    public void ReportProgress(double pct) => Progress = pct;

    [RelayCommand]
    private void Close() => _owner.RemoveToast(this);

    public void Detach() => _cts.Cancel();

    partial void OnProgressChanged(double? value)
    {
        OnPropertyChanged(nameof(IsProgressVisible));
    }
}

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly INotificationService _notificationService;
    private readonly IAutoUpdateService _autoUpdateService;
    private readonly IUpdateFlowService _updateFlow;
    private readonly ILocalDbService _db;
    private ToastItemViewModel? _updateToast;

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private string _windowTitle = "KermoLauncher";

    [ObservableProperty]
    private bool _isLibraryActive;

    [ObservableProperty]
    private bool _isSettingsActive;

    public ObservableCollection<ToastItemViewModel> Toasts { get; } = [];

    public string WindowVersion => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public LibraryViewModel LibraryVm { get; }
    public SettingsViewModel SettingsVm { get; }

    public MainWindowViewModel(
        LibraryViewModel libraryVm,
        SettingsViewModel settingsVm,
        INotificationService notificationService,
        IAutoUpdateService autoUpdateService,
        IUpdateFlowService updateFlow,
        ILocalDbService db)
    {
        _notificationService = notificationService;
        _autoUpdateService = autoUpdateService;
        _updateFlow = updateFlow;
        _db = db;

        LibraryVm = libraryVm;
        SettingsVm = settingsVm;

        CurrentView = LibraryVm;
        IsLibraryActive = true;

        _notificationService.NotificationRaised += OnNotificationRaised;
        _updateFlow.DownloadProgress += OnUpdateDownloadProgress;

        _ = LibraryVm.InitializeAsync();

        _ = CleanupPendingUpdateAsync();
        _ = CheckForUpdatesAsync();
    }

    private void OnUpdateDownloadProgress(double pct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (pct >= 100)
            {
                if (_updateToast != null)
                {
                    RemoveToast(_updateToast);
                    _updateToast = null;
                }
                return;
            }

            if (_updateToast == null)
            {
                _updateToast = new ToastItemViewModel(
                    new Notification(L["Updates.DownloadingTitle"], L["Updates.DownloadingMessage"], NotificationType.Info),
                    this,
                    autoDismiss: false);
                Toasts.Add(_updateToast);
            }
            _updateToast.ReportProgress(pct);
        });
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
        IsSettingsActive = value == SettingsVm;
    }

    [RelayCommand]
    private void ShowLibrary() => CurrentView = LibraryVm;

    [RelayCommand]
    private void ShowSettings() => CurrentView = SettingsVm;

    private async Task CleanupPendingUpdateAsync()
    {
        try
        {
            if (await _autoUpdateService.IsUpdatePendingAsync())
            {
                await _autoUpdateService.CleanupPendingUpdateAsync();
            }
        }
        catch
        {
            // Best effort
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var settings = await _db.GetSettingsAsync();
            if (!settings.AutoUpdate) return;

            var update = await _autoUpdateService.CheckForUpdatesAsync();
            if (update != null)
            {
                await _updateFlow.RunAsync(update);
            }
        }
        catch
        {
            // Ignore update check errors
        }
    }
}
