using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Services;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly ILogger<UpdateFlowService> _logger;

    public event Action<double>? DownloadProgress;

    public UpdateFlowService(
        IAutoUpdateService autoUpdateService,
        IDialogService dialogService,
        INotificationService notificationService,
        ILogger<UpdateFlowService> logger)
    {
        _autoUpdateService = autoUpdateService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _logger = logger;
        _l = App.Services!.GetRequiredService<ILocalizationService>();
    }

    public async Task RunAsync(UpdateInfo update)
    {
        try
        {
            var path = await DownloadAsync(update);

            var restart = await _dialogService.ShowConfirmAsync(
                _l["Updates.ReadyTitle"],
                string.Format(_l["Updates.ReadyMessage"], update.Version),
                _l["Updates.RestartNow"],
                _l["Updates.RestartLater"]);

            if (restart)
            {
                _notificationService.Show(_l["Updates.InstallingTitle"], _l["Updates.InstallingMessage"]);
                await _autoUpdateService.ApplyUpdateAsync(path);
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

    private async Task<string> DownloadAsync(UpdateInfo update)
    {
        var cachedPath = _autoUpdateService.GetCachedDownloadPath(update);
        if (File.Exists(cachedPath))
        {
            _logger.LogInformation("Reusing cached update file {Path}", cachedPath);
            return cachedPath;
        }

        return await _autoUpdateService.DownloadUpdateAsync(update, new Progress<double>(p => DownloadProgress?.Invoke(p)));
    }
}
