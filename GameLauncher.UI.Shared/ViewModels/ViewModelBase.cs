using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Core.Services.Interfaces;

namespace GameLauncher.UI.Shared.ViewModels;

/// <summary>
/// Shared by the launcher and the admin tool: both had their own identical copy, so every fix
/// had to be made twice.
/// </summary>
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
    /// ILocalizationService is a singleton, so a subscription that is never dropped keeps every
    /// ViewModel ever created alive for the lifetime of the process.
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
