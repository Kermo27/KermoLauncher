using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.AdminTool.Services;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Shared.ViewModels;
using Microsoft.Extensions.Logging;

namespace GameLauncher.AdminTool.ViewModels;

public partial class GameEditorViewModel : ViewModelBase
{
    private readonly MetadataGenerator _metadataGenerator;
    private readonly IFolderPicker _folders;
    private readonly ILogger<GameEditorViewModel> _logger;

    [ObservableProperty]
    private GameMetadata[] _games = [];

    [ObservableProperty]
    private GameMetadata? _selectedGame;

    [ObservableProperty]
    private string _scanFolderPath = "";

    [ObservableProperty]
    private string _publishFolder = "";

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isGenerating;

    public string GeneratedMetadataPath => string.IsNullOrWhiteSpace(ScanFolderPath)
        ? "metadata.json"
        : Path.Combine(ScanFolderPath, "metadata.json");

    public string GamesCountText => string.Format(L["Admin.Editor.GamesCount"], Games.Length);

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(GamesCountText));
        AttachLocalization();
    }

    partial void OnGamesChanged(GameMetadata[] value)
    {
        AttachLocalization();
        OnPropertyChanged(nameof(GamesCountText));
    }

    private void AttachLocalization()
    {
        foreach (var game in Games)
        {
            game.L = L;
        }
    }

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
    private bool _loading;

    public GameEditorViewModel(
        MetadataGenerator metadataGenerator,
        IFolderPicker folders,
        ILocalizationService localization,
        ILogger<GameEditorViewModel> logger)
        : base(localization)
    {
        _metadataGenerator = metadataGenerator;
        _folders = folders;
        _logger = logger;
        LoadState();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;

            _loading = true;
            var state = JsonSerializer.Deserialize<AdminToolState>(File.ReadAllText(StateFilePath), JsonOptions);
            if (state?.Games is { Length: > 0 })
            {
                Games = state.Games;
                ScanFolderPath = state.ScanFolderPath ?? "";
                PublishFolder = state.PublishFolder ?? "";
                SelectedGame = Games.FirstOrDefault(g => g.Id == state.SelectedGameId);
            }
            else if (!string.IsNullOrWhiteSpace(state?.PublishFolder))
            {
                PublishFolder = state.PublishFolder;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load AdminTool state");
        }
        finally
        {
            _loading = false;
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
                PublishFolder = PublishFolder,
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

    partial void OnScanFolderPathChanged(string value)
    {
        if (!_loading) SaveState();
    }

    partial void OnPublishFolderChanged(string value)
    {
        if (!_loading) SaveState();
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
            if (cts.IsCancellationRequested) return;
            try
            {
                SaveState();
                if (Games.Length > 0)
                {
                    _metadataGenerator.GenerateMetadataJsonAsync(Games, GeneratedMetadataPath).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to regenerate metadata");
            }
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private async Task BrowseScanFolderAsync()
    {
        var folder = await _folders.PickAsync(L["Admin.Editor.BrowseFolder"], ScanFolderPath);
        if (!string.IsNullOrEmpty(folder))
            ScanFolderPath = folder;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(ScanFolderPath))
        {
            ErrorText = L["Admin.Editor.ErrFolderPath"];
            return;
        }

        if (!Directory.Exists(ScanFolderPath))
        {
            ErrorText = string.Format(L["Admin.Editor.ErrFolderMissing"], ScanFolderPath);
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
                    game.Version = old.Version;
                    if (old.ScreenshotPaths.Length > 0)
                    {
                        var existing = old.ScreenshotPaths
                            .Where(p => File.Exists(Path.Combine(game.LocalFolder, p)))
                            .ToArray();
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
            if (games.Length > 0)
            {
                await _metadataGenerator.GenerateMetadataJsonAsync(games, GeneratedMetadataPath);
            }
            SaveState();
            StatusText = string.Format(L["Admin.Editor.ScanDone"], games.Length, preserved);
            if (games.Length == 0)
            {
                ErrorText = L["Admin.Editor.ErrNoFiles"];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan folder");
            ErrorText = string.Format(L["Admin.Editor.ErrScan"], ex.Message);
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
            LocalFolder = "",
            ManifestUrl = "nowa-gra/manifest.json",
            RemoteFolder = "nowa-gra",
            Files = []
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
            ErrorText = L["Admin.Editor.ErrNoGames"];
            return;
        }

        IsGenerating = true;
        ErrorText = null;
        try
        {
            await _metadataGenerator.GenerateMetadataJsonAsync(Games, GeneratedMetadataPath);
            StatusText = string.Format(L["Admin.Editor.GenerateDone"], GeneratedMetadataPath, Games.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate metadata");
            ErrorText = string.Format(L["Admin.Editor.ErrGenerate"], ex.Message);
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
    [ObservableProperty] private string _localFolder = "";
    [ObservableProperty] private string _manifestUrl = "";
    [ObservableProperty] private string _remoteFolder = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private GameFile[] _files = [];
    [ObservableProperty] private LaunchConfig? _launchConfig = null;

    [JsonIgnore]
    public ILocalizationService? L { get; set; }

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
    public string ScreenshotCountText => L == null
        ? ""
        : string.Format(L["Admin.Editor.ScreenshotCount"], ScreenshotPaths.Length);

    partial void OnScreenshotPathsChanged(string[] value) => OnPropertyChanged(nameof(ScreenshotCountText));

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
    public string PublishFolder { get; set; } = "";
    public string? SelectedGameId { get; set; }
}
