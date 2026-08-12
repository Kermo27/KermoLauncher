using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Core.Services.Interfaces;

namespace GameLauncher.AdminTool.ViewModels;

public partial class ViewModelBase : ObservableObject, IDisposable
{
    private bool _disposed;

    public ILocalizationService L { get; }

    protected ViewModelBase(ILocalizationService localization)
    {
        L = localization;
        L.LanguageChanged += OnLanguageChanged;
    }

    protected virtual void OnLanguageChanged() => OnPropertyChanged(nameof(L));

    /// <summary>
    /// ILocalizationService jest singletonem, więc niezdjęta subskrypcja trzymała każdy
    /// utworzony ViewModel do końca życia procesu.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        L.LanguageChanged -= OnLanguageChanged;
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    protected virtual void DisposeCore()
    {
    }
}
