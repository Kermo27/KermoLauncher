using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI.Services;

public interface IUpdateFlowService
{
    event Action<double>? DownloadProgress;
    Task RunAsync(UpdateInfo update);
}

public class UpdateFlowService : IUpdateFlowService
{
    private readonly IAutoUpdateService _autoUpdateService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly ILocalizationService _l;
    private readonly IAppShutdown _shutdown;
    private readonly ILogger<UpdateFlowService> _logger;

    public event Action<double>? DownloadProgress;

    public UpdateFlowService(
        IAutoUpdateService autoUpdateService,
        IDialogService dialogService,
        INotificationService notificationService,
        ILocalizationService localization,
        IAppShutdown shutdown,
        ILogger<UpdateFlowService> logger)
    {
        _autoUpdateService = autoUpdateService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _l = localization;
        _shutdown = shutdown;
        _logger = logger;
    }

    public async Task RunAsync(UpdateInfo update)
    {
        try
        {
            var path = await _autoUpdateService.DownloadUpdateAsync(
                update, new Progress<double>(p => DownloadProgress?.Invoke(p)));

            var restart = await _dialogService.ShowConfirmAsync(
                _l["Updates.ReadyTitle"],
                string.Format(_l["Updates.ReadyMessage"], update.Version),
                _l["Updates.RestartNow"],
                _l["Updates.RestartLater"]);

            if (restart)
            {
                _notificationService.Show(_l["Updates.InstallingTitle"], _l["Updates.InstallingMessage"]);
                await _autoUpdateService.ApplyUpdateAsync(path);
                _shutdown.RequestShutdown();
            }
            else
            {
                _notificationService.Show(_l["Updates.InstallLaterTitle"], _l["Updates.InstallLaterMessage"]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update flow failed");
            _notificationService.Show(_l["Updates.InstallFailedTitle"],
                string.Format(_l["Updates.InstallFailedMessage"], ex.Message), NotificationType.Error);
        }
    }
}
