namespace GameLauncher.Tests;

using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.UI.ViewModels;
using Xunit;

/// <summary>
/// ILocalizationService jest singletonem, więc ViewModel, który nie zdejmie subskrypcji
/// LanguageChanged, zostaje w pamięci do końca życia procesu.
/// </summary>
public class ViewModelLifetimeTests
{
    private sealed class ProbeViewModel : ViewModelBase
    {
        public int LanguageChanges { get; private set; }

        public ProbeViewModel(ILocalizationService localization) : base(localization)
        {
        }

        protected override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            LanguageChanges++;
        }
    }

    [Fact]
    public void Dispose_StopsListeningToLanguageChanges()
    {
        var localization = new LocalizationService();
        var vm = new ProbeViewModel(localization);

        localization.SetLanguage("pl");
        Assert.Equal(1, vm.LanguageChanges);

        vm.Dispose();
        localization.SetLanguage("en");

        Assert.Equal(1, vm.LanguageChanges);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var localization = new LocalizationService();
        var vm = new ProbeViewModel(localization);

        vm.Dispose();
        vm.Dispose();

        localization.SetLanguage("pl");
        Assert.Equal(0, vm.LanguageChanges);
    }
}
