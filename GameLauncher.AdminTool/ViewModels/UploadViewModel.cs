using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Services.Interfaces;
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

    public UploadViewModel(IWebDavService webDav, GameEditorViewModel gameEditor, ILogger<UploadViewModel> logger)
    {
        _webDav = webDav;
        _gameEditor = gameEditor;
        _logger = logger;
    }

    private List<(string LocalPath, string RemotePath)> BuildFileList(string baseUrl)
    {
        var files = new List<(string, string)>();

        foreach (var game in _gameEditor.Games)
        {
            if (!string.IsNullOrWhiteSpace(game.LocalZipPath) && File.Exists(game.LocalZipPath))
            {
                files.Add((game.LocalZipPath, $"{baseUrl}/{game.RemoteZipPath}"));
            }
            foreach (var shot in game.ScreenshotPaths)
            {
                if (File.Exists(shot))
                {
                    files.Add((shot, $"{baseUrl}/{game.RemoteFolder}/screenshots/{Path.GetFileName(shot)}"));
                }
            }
        }

        return files;
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
            ErrorText = "Uzupełnij adres WebDAV, nazwę użytkownika i hasło aplikacji.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_gameEditor.GeneratedMetadataPath) || !File.Exists(_gameEditor.GeneratedMetadataPath))
        {
            ErrorText = "Brak pliku metadata.json. Wygeneruj go w zakładce Edytor gier.";
            return;
        }

        if (_gameEditor.Games.Length == 0)
        {
            ErrorText = "Brak gier do wysłania. Zeskanuj folder w zakładce Edytor gier.";
            return;
        }

        IsUploading = true;
        UploadProgress = 0;
        ErrorText = null;
        StatusText = "Przygotowywanie listy plików...";
        CurrentFileText = null;

        try
        {
            var baseUrl = $"{url.TrimEnd('/')}/Games";
            var files = BuildFileList(baseUrl);

            if (files.Count == 0)
            {
                ErrorText = "Nie znaleziono lokalnych plików .zip do wysłania (sprawdź ścieżki ZIP w edytorze gier).";
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

            // 2. Upload game files
            var total = files.Count + 1;
            var completed = 0;
            foreach (var (localPath, remotePath) in files)
            {
                CurrentFileText = $"Przesyłanie: {Path.GetFileName(localPath)} ({new FileInfo(localPath).Length / 1024 / 1024} MB)";
                await _webDav.UploadFileAsync(remotePath, localPath, user, password);
                completed++;
                UploadProgress = completed * 90.0 / total;
            }

            // 3. Upload metadata.json last
            CurrentFileText = "Przesyłanie: metadata.json";
            await _webDav.UploadFileAsync($"{baseUrl}/metadata.json", _gameEditor.GeneratedMetadataPath, user, password);
            completed++;
            UploadProgress = completed * 100.0 / total;

            StatusText = "Gotowe! Wszystkie pliki zostały wysłane na Nextcloud.";
            CurrentFileText = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload failed");
            ErrorText = $"Błąd wysyłki: {ex.Message}";
            StatusText = "Wysyłka nie powiodła się.";
        }
        finally
        {
            IsUploading = false;
        }
    }
}