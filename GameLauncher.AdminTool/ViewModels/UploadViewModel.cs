using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.AdminTool.Services;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Shared.ViewModels;
using Microsoft.Extensions.Logging;

namespace GameLauncher.AdminTool.ViewModels;

public partial class UploadViewModel : ViewModelBase
{
    private readonly IWebDavService _webDav;
    private readonly ILogger<UploadViewModel> _logger;
    private readonly GameEditorViewModel _gameEditor;

    [ObservableProperty]
    private string _nextcloudUrl = "";

    [ObservableProperty]
    private string _nextcloudUser = "";

    [ObservableProperty]
    private string _nextcloudPassword = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _currentFileText;

    [ObservableProperty]
    private double _uploadProgress;

    [ObservableProperty]
    private bool _isUploading;

    public bool IsNotUploading => !IsUploading;

    public GameEditorViewModel GameEditor => _gameEditor;

    partial void OnIsUploadingChanged(bool value) => OnPropertyChanged(nameof(IsNotUploading));

    public UploadViewModel(
        IWebDavService webDav,
        GameEditorViewModel gameEditor,
        ILocalizationService localization,
        ILogger<UploadViewModel> logger)
        : base(localization)
    {
        _webDav = webDav;
        _gameEditor = gameEditor;
        _logger = logger;
    }

    private record UploadItem(string LocalPath, string RemotePath);

    private async Task<List<UploadItem>> BuildFileListAsync(string baseUrl, string user, string password, CancellationToken ct = default)
    {
        var files = new List<UploadItem>();

        foreach (var game in _gameEditor.Games)
        {
            var gameBase = $"{baseUrl}/{game.RemoteFolder}";
            var remoteManifest = await TryGetRemoteManifestAsync($"{gameBase}/manifest.json", user, password, ct);

            // Game files (delta: skip files whose path+size+hash match the remote manifest)
            var toUpload = UploadDiff.FilesToUpload(game.Files, remoteManifest);
            foreach (var file in toUpload)
            {
                if (string.IsNullOrWhiteSpace(game.LocalFolder)) continue;
                var localPath = Path.Combine(game.LocalFolder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath)) continue;
                files.Add(new UploadItem(localPath, $"{gameBase}/{file.Path}"));
            }

            // Screenshots (always upload - small files)
            foreach (var shot in game.ScreenshotPaths)
            {
                if (string.IsNullOrWhiteSpace(game.LocalFolder)) continue;
                var localPath = Path.Combine(game.LocalFolder, shot.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath)) continue;
                var remotePath = $"{gameBase}/screenshots/{Path.GetFileName(shot)}";
                files.Add(new UploadItem(localPath, remotePath));
            }

            // Manifest
            if (string.IsNullOrWhiteSpace(game.LocalFolder)) continue;
            var localManifest = Path.Combine(game.LocalFolder, "manifest.json");
            if (File.Exists(localManifest))
            {
                files.Add(new UploadItem(localManifest, $"{gameBase}/manifest.json"));
            }
        }

        return files;
    }

    private async Task<GameManifest?> TryGetRemoteManifestAsync(string manifestUrl, string user, string password, CancellationToken ct = default)
    {
        try
        {
            return await _webDav.DownloadManifestAsync(manifestUrl, ct, user, password);
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (IsUploading) return;

        var url = NextcloudUrl?.Trim() ?? "";
        var user = NextcloudUser?.Trim() ?? "";
        var password = NextcloudPassword ?? "";

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            ErrorText = L["Admin.Upload.ErrCredentials"];
            return;
        }

        if (string.IsNullOrWhiteSpace(_gameEditor.GeneratedMetadataPath) || !File.Exists(_gameEditor.GeneratedMetadataPath))
        {
            ErrorText = L["Admin.Upload.ErrMetadata"];
            return;
        }

        if (_gameEditor.Games.Length == 0)
        {
            ErrorText = L["Admin.Upload.ErrGames"];
            return;
        }

        IsUploading = true;
        UploadProgress = 0;
        ErrorText = null;
        StatusText = L["Admin.Upload.Preparing"];
        CurrentFileText = null;

        try
        {
            var baseUrl = $"{url.TrimEnd('/')}/Games";
            var files = await BuildFileListAsync(baseUrl, user, password);

            if (files.Count == 0)
            {
            ErrorText = L["Admin.Upload.ErrNoFiles"];
            StatusText = "";
            return;
            }

            // 1. Create directory structure (metadata goes last)
            _logger.LogInformation("Creating directories under {BaseUrl}", baseUrl);
            await _webDav.CreateDirectoryAsync(baseUrl, user, password);
            foreach (var game in _gameEditor.Games)
            {
                await _webDav.CreateDirectoryAsync($"{baseUrl}/{game.RemoteFolder}", user, password);
                if (game.ScreenshotPaths.Length > 0)
                {
                    await _webDav.CreateDirectoryAsync($"{baseUrl}/{game.RemoteFolder}/screenshots", user, password);
                }
            }

            // 2. Upload files
            var total = files.Count + 1;
            var completed = 0;
            foreach (var item in files)
            {
                CurrentFileText = string.Format(L["Admin.Upload.UploadingFile"],
                    Path.GetFileName(item.LocalPath), new FileInfo(item.LocalPath).Length / 1024 / 1024);
                await _webDav.UploadFileAsync(item.RemotePath, item.LocalPath, user, password);
                completed++;
                UploadProgress = completed * 90.0 / total;
            }

            // 3. Upload metadata.json last
            CurrentFileText = L["Admin.Upload.UploadingMetadata"];
            await _webDav.UploadFileAsync($"{baseUrl}/metadata.json", _gameEditor.GeneratedMetadataPath, user, password);
            completed++;
            UploadProgress = completed * 100.0 / total;

            StatusText = string.Format(L["Admin.Upload.Done"], files.Count);
            CurrentFileText = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload failed");
            ErrorText = string.Format(L["Admin.Upload.Error"], ex.Message);
            StatusText = L["Admin.Upload.Failed"];
        }
        finally
        {
            IsUploading = false;
        }
    }
}
