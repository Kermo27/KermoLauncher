using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncher.UI.ViewModels;

public partial class ViewModelBase : ObservableObject
{
    public ILocalizationService L { get; }

    protected ViewModelBase()
    {
        L = App.Services!.GetRequiredService<ILocalizationService>();
        L.LanguageChanged += OnLanguageChanged;
    }

    protected virtual void OnLanguageChanged() => OnPropertyChanged(nameof(L));
}
