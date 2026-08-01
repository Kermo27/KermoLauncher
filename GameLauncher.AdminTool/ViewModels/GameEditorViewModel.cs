using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.AdminTool.Services;
using GameLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.AdminTool.ViewModels;

public partial class GameEditorViewModel : ViewModelBase
{
    private readonly MetadataGenerator _metadataGenerator;
    private readonly ILogger<GameEditorViewModel> _logger;

    [ObservableProperty]
    private GameMetadata[] _games = [];

    [ObservableProperty]
    private GameMetadata? _selectedGame;

    [ObservableProperty]
    private string _scanFolderPath = "";

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isGenerating;

    public string GeneratedMetadataPath { get; } = "metadata.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string StateFilePath
    {
        get
        {
            var dir = GameLauncher.Core.Utils.AppPaths.DataDirectory;
            var file = Path.Combine(dir, "admintool-games.json");
            var oldFile = Path.Combine(Path.GetDirectoryName(dir)!, "GameLauncher", "admintool-games.json");
            if (!File.Exists(file) && File.Exists(oldFile))
            {
                try
                {
                    File.Move(oldFile, file);
                }
                catch
                {
                    // Ignore migration errors
                }
            }
            return file;
        }
    }

    private CancellationTokenSource? _saveCts;
    private GameMetadata? _selectedForSave;

    public GameEditorViewModel(
        MetadataGenerator metadataGenerator,
        ILogger<GameEditorViewModel> logger)
    {
        _metadataGenerator = metadataGenerator;
        _logger = logger;
        LoadState();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;

            var state = JsonSerializer.Deserialize<AdminToolState>(File.ReadAllText(StateFilePath), JsonOptions);
            if (state?.Games is { Length: > 0 })
            {
                Games = state.Games;
                ScanFolderPath = state.ScanFolderPath ?? "";
                SelectedGame = Games.FirstOrDefault(g => g.Id == state.SelectedGameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load AdminTool state");
        }
    }

    private void SaveState()
    {
        try
        {
            var state = new AdminToolState
            {
                Games = Games,
                ScanFolderPath = ScanFolderPath,
                SelectedGameId = SelectedGame?.Id
            };
            var json = JsonSerializer.Serialize(state, JsonOptions);
            var dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save AdminTool state");
        }
    }

    partial void OnSelectedGameChanged(GameMetadata? value)
    {
        if (_selectedForSave != null)
        {
            _selectedForSave.PropertyChanged -= OnEditedGamePropertyChanged;
        }
        _selectedForSave = value;
        if (value != null)
        {
            value.PropertyChanged += OnEditedGamePropertyChanged;
        }
    }

    private void OnEditedGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        _ = Task.Delay(600, cts.Token).ContinueWith(_ =>
        {
            if (!cts.IsCancellationRequested) SaveState();
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(ScanFolderPath))
        {
            ErrorText = "Podaj ścieżkę do folderu z grami (np. /home/user/Nextcloud/Games).";
            return;
        }

        if (!Directory.Exists(ScanFolderPath))
        {
            ErrorText = $"Folder nie istnieje: {ScanFolderPath}";
            return;
        }

        IsScanning = true;
        ErrorText = null;
        StatusText = null;
        try
        {
            var games = await _metadataGenerator.ScanFolderAsync(ScanFolderPath);
            var oldById = Games.ToDictionary(g => g.Id);
            var preserved = 0;
            foreach (var game in games)
            {
                if (oldById.TryGetValue(game.Id, out var old))
                {
                    game.Description = old.Description;
                    game.Tags = old.Tags;
                    game.Dependencies = old.Dependencies;
                    game.LaunchConfig = old.LaunchConfig;
                    game.Name = old.Name;
                    if (!string.IsNullOrWhiteSpace(old.LocalZipPath) && File.Exists(old.LocalZipPath))
                    {
                        game.LocalZipPath = old.LocalZipPath;
                    }
                    if (old.ScreenshotPaths.Length > 0)
                    {
                        var existing = old.ScreenshotPaths.Where(File.Exists).ToArray();
                        if (existing.Length > 0)
                        {
                            game.ScreenshotPaths = existing.Concat(game.ScreenshotPaths).Distinct().ToArray();
                        }
                    }
                    preserved++;
                }
            }
            Games = games;
            if (SelectedGame != null)
            {
                SelectedGame = Games.FirstOrDefault(g => g.Id == SelectedGame.Id);
            }
            SaveState();
            StatusText = $"Znaleziono {games.Length} gier w folderze. Zachowano ręczne opisy dla {preserved} gier.";
            if (games.Length == 0)
            {
                ErrorText = "Brak plików .zip w podfolderach. Każda gra musi być w osobnym podfolderze z archiwum .zip.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan folder");
            ErrorText = $"Błąd skanowania: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var game = new GameMetadata
        {
            Id = "nowa-gra",
            Name = "Nowa gra",
            Version = "1.0.0",
            Description = "",
            Tags = [],
            Dependencies = [],
            ScreenshotPaths = [],
            LocalZipPath = "",
            RemoteZipPath = "nowa-gra/nowa-gra-v1.0.0.zip",
            RemoteFolder = "nowa-gra"
        };
        
        var list = Games.ToList();
        list.Add(game);
        Games = list.ToArray();
        SelectedGame = game;
        SaveState();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveGame(GameMetadata game)
    {
        var list = Games.ToList();
        list.Remove(game);
        Games = list.ToArray();
        if (SelectedGame == game)
            SelectedGame = null;
        SaveState();
    }

    [RelayCommand]
    private async Task GenerateMetadataAsync()
    {
        if (Games.Length == 0)
        {
            ErrorText = "Brak gier. Zeskanuj najpierw folder z grami.";
            return;
        }

        IsGenerating = true;
        ErrorText = null;
        try
        {
            await _metadataGenerator.GenerateMetadataJsonAsync(Games, GeneratedMetadataPath);
            StatusText = $"Wygenerowano {GeneratedMetadataPath} ({Games.Length} gier).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate metadata");
            ErrorText = $"Błąd generowania metadata.json: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }
}

public partial class GameMetadata : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string[] _tags = [];
    [ObservableProperty] private string[] _dependencies = [];
    [ObservableProperty] private string[] _screenshotPaths = [];
    [ObservableProperty] private string _localZipPath = "";
    [ObservableProperty] private string _remoteZipPath = "";
    [ObservableProperty] private string _remoteFolder = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private string _sha256 = "";
    [ObservableProperty] private LaunchConfig? _launchConfig = null;

    [JsonIgnore]
    public string TagsString
    {
        get => string.Join(", ", Tags);
        set => Tags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [JsonIgnore]
    public string DependenciesString
    {
        get => string.Join(", ", Dependencies);
        set => Dependencies = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [JsonIgnore]
    public string SizeText => SizeBytes > 0 ? $"{SizeBytes / (1024d * 1024d * 1024d):F1} GB" : "";

    [JsonIgnore]
    public string LaunchExecutable
    {
        get => LaunchConfig?.ExecutablePath ?? "";
        set => LaunchConfig = LaunchConfig == null
            ? new LaunchConfig(value)
            : LaunchConfig with { ExecutablePath = value };
    }

    [JsonIgnore]
    public string LaunchWorkingDir
    {
        get => LaunchConfig?.WorkingDirectory ?? "";
        set => LaunchConfig = LaunchConfig == null
            ? new LaunchConfig("", WorkingDirectory: string.IsNullOrWhiteSpace(value) ? null : value)
            : LaunchConfig with { WorkingDirectory = string.IsNullOrWhiteSpace(value) ? null : value };
    }

    [JsonIgnore]
    public string LaunchArgsString
    {
        get => LaunchConfig?.LaunchArgs != null ? string.Join(", ", LaunchConfig.LaunchArgs) : "";
        set
        {
            var args = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            LaunchConfig = LaunchConfig == null
                ? new LaunchConfig("", LaunchArgs: args.Length > 0 ? args : null)
                : LaunchConfig with { LaunchArgs = args.Length > 0 ? args : null };
        }
    }
}
internal class AdminToolState
{
    public GameMetadata[] Games { get; set; } = [];
    public string ScanFolderPath { get; set; } = "";
    public string? SelectedGameId { get; set; }
}
