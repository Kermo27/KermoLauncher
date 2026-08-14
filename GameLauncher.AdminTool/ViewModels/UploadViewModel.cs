using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.AdminTool.Services;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.Shared.ViewModels;
using Microsoft.Extensions.Logging;

namespace GameLauncher.AdminTool.ViewModels;

public partial class UploadViewModel : ViewModelBase
{
    private readonly LibraryPublisher _publisher;
    private readonly MetadataGenerator _metadata;
    private readonly IFolderPicker _folders;
    private readonly ILogger<UploadViewModel> _logger;
    private readonly GameEditorViewModel _gameEditor;

    [ObservableProperty]
    private string _destFolder = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _currentFileText;

    [ObservableProperty]
    private double _uploadProgress;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasCompared;

    public bool IsIdle => !IsBusy;

    public GameEditorViewModel GameEditor => _gameEditor;

    public ObservableCollection<GameSyncRow> Plans { get; } = [];

    public bool HasChanges => Plans.Any(p => !p.IsUpToDate);

    public string TotalsText
    {
        get
        {
            if (!HasCompared || Plans.Count == 0) return "";
            var added = Plans.Sum(p => p.AddedCount);
            var changed = Plans.Sum(p => p.ChangedCount);
            var removed = Plans.Sum(p => p.RemovedCount);
            var bytes = Plans.Sum(p => p.BytesToCopy);
            return string.Format(L["Admin.Upload.Totals"], added, changed, removed, FormatBytes(bytes));
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    public UploadViewModel(
        LibraryPublisher publisher,
        MetadataGenerator metadata,
        IFolderPicker folders,
        GameEditorViewModel gameEditor,
        ILocalizationService localization,
        ILogger<UploadViewModel> logger)
        : base(localization)
    {
        _publisher = publisher;
        _metadata = metadata;
        _folders = folders;
        _gameEditor = gameEditor;
        _logger = logger;
        DestFolder = string.IsNullOrWhiteSpace(gameEditor.PublishFolder)
            ? GuessNextcloudGamesFolder()
            : gameEditor.PublishFolder;
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(TotalsText));
        foreach (var row in Plans)
            row.Refresh(L);
    }

    partial void OnDestFolderChanged(string value)
    {
        _gameEditor.PublishFolder = value?.Trim() ?? "";
        HasCompared = false;
        Plans.Clear();
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(TotalsText));
    }

    [RelayCommand]
    private async Task BrowseDestAsync()
    {
        var folder = await _folders.PickAsync(L["Admin.Upload.BrowseDest"], DestFolder);
        if (!string.IsNullOrEmpty(folder))
            DestFolder = folder;
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (IsBusy) return;
        ErrorText = ValidateReady();
        if (ErrorText != null) return;

        IsBusy = true;
        StatusText = L["Admin.Upload.Preparing"];
        CurrentFileText = null;
        UploadProgress = 0;
        try
        {
            Plans.Clear();
            foreach (var game in _gameEditor.Games)
            {
                var plan = await _publisher.CompareAsync(game, DestFolder.Trim());
                Plans.Add(new GameSyncRow(plan, L));
            }

            HasCompared = true;
            OnPropertyChanged(nameof(HasChanges));
            OnPropertyChanged(nameof(TotalsText));
            StatusText = HasChanges ? TotalsText : L["Admin.Upload.AlreadyInSync"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compare failed");
            ErrorText = string.Format(L["Admin.Upload.Error"], ex.Message);
            StatusText = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (IsBusy) return;
        ErrorText = ValidateReady();
        if (ErrorText != null) return;

        if (!HasCompared)
            await CompareAsync();
        if (ErrorText != null || IsBusy) return;

        var dirty = Plans.Where(p => !p.IsUpToDate).ToArray();
        if (dirty.Length == 0)
        {
            StatusText = L["Admin.Upload.AlreadyInSync"];
            return;
        }

        IsBusy = true;
        UploadProgress = 0;
        ErrorText = null;
        try
        {
            Directory.CreateDirectory(DestFolder.Trim());
            var published = new List<GameMetadata>();
            var copied = 0;

            for (var i = 0; i < dirty.Length; i++)
            {
                var row = dirty[i];
                var game = _gameEditor.Games.First(g => g.Id == row.Plan.GameId);
                var index = i;
                var progress = new Progress<PublishProgress>(p =>
                {
                    CurrentFileText = string.Format(L["Admin.Upload.CopyingFile"], p.RelativePath);
                    var frac = (index + (double)p.Completed / Math.Max(p.Total, 1)) / dirty.Length;
                    UploadProgress = frac * 90;
                });
                await _publisher.PublishAsync(DestFolder.Trim(), game, row.Plan, progress);
                published.Add(game);
                copied += row.Plan.ToCopy.Count() + row.Plan.ToDelete.Count();
            }

            CurrentFileText = L["Admin.Upload.UploadingMetadata"];
            await _metadata.UpsertCatalogAsync(DestFolder.Trim(), published.ToArray());
            UploadProgress = 100;

            await CompareUnlockedAsync();
            StatusText = string.Format(L["Admin.Upload.Done"], copied);
            CurrentFileText = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publish failed");
            ErrorText = string.Format(L["Admin.Upload.Error"], ex.Message);
            StatusText = L["Admin.Upload.Failed"];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompareUnlockedAsync()
    {
        Plans.Clear();
        foreach (var game in _gameEditor.Games)
        {
            var plan = await _publisher.CompareAsync(game, DestFolder.Trim());
            Plans.Add(new GameSyncRow(plan, L));
        }

        HasCompared = true;
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(TotalsText));
    }

    private string? ValidateReady()
    {
        if (string.IsNullOrWhiteSpace(DestFolder))
            return L["Admin.Upload.ErrDest"];
        if (_gameEditor.Games.Length == 0)
            return L["Admin.Upload.ErrGames"];
        if (PathsEqual(DestFolder, _gameEditor.ScanFolderPath))
            return L["Admin.Upload.ErrSameFolder"];
        return null;
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim()),
                Path.GetFullPath(b.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string GuessNextcloudGamesFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Nextcloud", "Games");
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024d;
        if (kb < 1024) return $"{kb:0} KB";
        var mb = kb / 1024d;
        if (mb < 1024) return $"{mb:0.0} MB";
        return $"{mb / 1024d:0.00} GB";
    }
}

public sealed class GameSyncRow
{
    public GameSyncPlan Plan { get; }
    public string GameName => Plan.GameName;
    public bool IsUpToDate => Plan.IsUpToDate;
    public int AddedCount => Plan.AddedCount;
    public int ChangedCount => Plan.ChangedCount;
    public int RemovedCount => Plan.RemovedCount;
    public long BytesToCopy => Plan.BytesToCopy;

    public string Summary { get; private set; } = "";
    public string Details { get; private set; } = "";

    public GameSyncRow(GameSyncPlan plan, ILocalizationService l)
    {
        Plan = plan;
        Refresh(l);
    }

    public void Refresh(ILocalizationService l)
    {
        if (IsUpToDate)
        {
            Summary = l["Admin.Upload.GameInSync"];
            Details = "";
            return;
        }

        Summary = string.Format(l["Admin.Upload.GameSummary"],
            AddedCount, ChangedCount, RemovedCount, UploadViewModel.FormatBytes(BytesToCopy));
        Details = string.Join(Environment.NewLine, Plan.Changes.Take(12).Select(c =>
        {
            var mark = c.Kind switch
            {
                SyncChangeKind.Added => "+",
                SyncChangeKind.Changed => "~",
                _ => "−"
            };
            return $"{mark} {c.RelativePath}";
        }));
        if (Plan.Changes.Count > 12)
            Details += Environment.NewLine + string.Format(l["Admin.Upload.MoreFiles"], Plan.Changes.Count - 12);
    }
}
