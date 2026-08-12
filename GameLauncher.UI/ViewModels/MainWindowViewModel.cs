using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;

namespace GameLauncher.UI.ViewModels;

public partial class ToastItemViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public string Title { get; }
    public string Message { get; }
    public NotificationType Type { get; }

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressVisible;

    public bool IsInfo => Type == NotificationType.Info;
    public bool IsSuccess => Type == NotificationType.Success;
    public bool IsWarning => Type == NotificationType.Warning;
    public bool IsError => Type == NotificationType.Error;

    public string Icon => Type switch
    {
        NotificationType.Success => "\u2705",
        NotificationType.Warning => "\u26A0\uFE0F",
        NotificationType.Error => "\u274C",
        _ => "\u2139\uFE0F"
    };

    public ToastItemViewModel(
        Notification notification,
        MainWindowViewModel owner,
        IUiDispatcher dispatcher,
        bool autoDismiss = true)
    {
        _owner = owner;
        _dispatcher = dispatcher;
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
        try
        {
            // The token goes into Delay: the wait used to run to completion even after the toast closed.
            await Task.Delay(GetDurationMs(Type), _cts.Token);
            await _dispatcher.InvokeAsync(() => _owner.RemoveToast(this));
        }
        catch (OperationCanceledException)
        {
            // The toast was closed by hand or pushed out by newer notifications.
        }
    }

    public void ReportProgress(double pct)
    {
        Progress = pct;
        IsProgressVisible = true;
    }

    [RelayCommand]
    private void Close() => _owner.RemoveToast(this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly INotificationService _notificationService;
    private readonly IAutoUpdateService _autoUpdateService;
    private readonly IUpdateFlowService _updateFlow;
    private readonly ILocalDbService _db;
    private readonly IUiDispatcher _dispatcher;
    private ToastItemViewModel? _updateToast;

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private string _windowTitle = "KermoLauncher";

    [ObservableProperty]
    private bool _isLibraryActive;

    [ObservableProperty]
    private bool _isSettingsActive;

    [ObservableProperty]
    private bool _isOnboardingActive;

    public ObservableCollection<ToastItemViewModel> Toasts { get; } = [];

    public string WindowVersion => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public LibraryViewModel LibraryVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public OnboardingViewModel OnboardingVm { get; }

    public MainWindowViewModel(
        LibraryViewModel libraryVm,
        SettingsViewModel settingsVm,
        OnboardingViewModel onboardingVm,
        INotificationService notificationService,
        IAutoUpdateService autoUpdateService,
        IUpdateFlowService updateFlow,
        ILocalDbService db,
        ILocalizationService localization,
        IUiDispatcher dispatcher)
        : base(localization)
    {
        _notificationService = notificationService;
        _autoUpdateService = autoUpdateService;
        _updateFlow = updateFlow;
        _db = db;
        _dispatcher = dispatcher;

        LibraryVm = libraryVm;
        SettingsVm = settingsVm;
        OnboardingVm = onboardingVm;
        OnboardingVm.Completed = CompleteOnboardingAsync;

        CurrentView = LibraryVm;
        IsLibraryActive = true;

        _notificationService.NotificationRaised += OnNotificationRaised;
        _updateFlow.DownloadProgress += OnUpdateDownloadProgress;
    }

    /// <summary>
    /// Everything that touches the network or disk happens here rather than in the constructor,
    /// so exceptions have somewhere to surface and showing the window does not wait on I/O.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SettingsVm.InitializeAsync();

        var settings = await _db.GetSettingsAsync();
        if (settings.NeedsOnboarding)
        {
            OnboardingVm.Initialize();
            IsOnboardingActive = true;
            CurrentView = OnboardingVm;
            return;
        }

        await ContinueAfterOnboardingAsync(ct);
    }

    public async Task CompleteOnboardingAsync()
    {
        await SettingsVm.InitializeAsync();
        IsOnboardingActive = false;
        CurrentView = LibraryVm;
        await ContinueAfterOnboardingAsync();
    }

    private async Task ContinueAfterOnboardingAsync(CancellationToken ct = default)
    {
        await LibraryVm.InitializeAsync();
        await CleanupPendingUpdateAsync();
        await CheckForUpdatesAsync(ct);
    }

    private void OnUpdateDownloadProgress(double pct)
    {
        _dispatcher.Post(() =>
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
                    _dispatcher,
                    autoDismiss: false);
                Toasts.Add(_updateToast);
            }
            _updateToast.ReportProgress(pct);
        });
    }

    private void OnNotificationRaised(Notification notification)
    {
        _dispatcher.Post(() =>
        {
            while (Toasts.Count >= 4)
            {
                Toasts[0].Dispose();
                Toasts.RemoveAt(0);
            }
            Toasts.Add(new ToastItemViewModel(notification, this, _dispatcher));
        });
    }

    public void RemoveToast(ToastItemViewModel toast)
    {
        toast.Dispose();
        Toasts.Remove(toast);
    }

    partial void OnCurrentViewChanged(ViewModelBase value)
    {
        IsLibraryActive = !IsOnboardingActive && value == LibraryVm;
        IsSettingsActive = !IsOnboardingActive && value == SettingsVm;
    }

    [RelayCommand]
    private void ShowLibrary()
    {
        if (IsOnboardingActive) return;
        CurrentView = LibraryVm;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        if (IsOnboardingActive) return;
        CurrentView = SettingsVm;
    }

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

    private async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _db.GetSettingsAsync();
            if (!settings.AutoUpdate) return;

            var update = await _autoUpdateService.CheckForUpdatesAsync(ct);
            if (update != null)
            {
                await _updateFlow.RunAsync(update);
            }
        }
        catch (Exception)
        {
            // A missing network must not take the application startup down with it.
        }
    }

    protected override void DisposeCore()
    {
        _notificationService.NotificationRaised -= OnNotificationRaised;
        _updateFlow.DownloadProgress -= OnUpdateDownloadProgress;

        foreach (var toast in Toasts)
        {
            toast.Dispose();
        }
        Toasts.Clear();

        LibraryVm.Dispose();
        SettingsVm.Dispose();
        OnboardingVm.Dispose();
    }
}
