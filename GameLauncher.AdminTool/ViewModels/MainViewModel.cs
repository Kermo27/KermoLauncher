using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameLauncher.AdminTool.ViewModels;

public partial class MainViewModel : ViewModelBase
{

    [ObservableProperty]
    private ViewModelBase _currentView = null!;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isGameEditorActive;

    [ObservableProperty]
    private bool _isUploadActive;

    public GameEditorViewModel GameEditorVm { get; }
    public UploadViewModel UploadVm { get; }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        StatusText = L["Admin.Ready"];
    }

    public MainViewModel(
        GameEditorViewModel gameEditorVm,
        UploadViewModel uploadVm)
    {
        GameEditorVm = gameEditorVm;
        UploadVm = uploadVm;

        CurrentView = GameEditorVm;
        IsGameEditorActive = true;
        StatusText = L["Admin.Ready"];
    }

    partial void OnCurrentViewChanged(ViewModelBase value)
    {
        IsGameEditorActive = value == GameEditorVm;
        IsUploadActive = value == UploadVm;
    }

    [RelayCommand]
    private void ShowGameEditor() => CurrentView = GameEditorVm;

    [RelayCommand]
    private void ShowUpload() => CurrentView = UploadVm;
}