using System.Globalization;
using System.Resources;
using GameLauncher.Core.Services.Interfaces;

namespace GameLauncher.Core.Services;

public class LocalizationService : ILocalizationService
{
    private readonly ResourceManager _resources =
        new("GameLauncher.Core.Resources.Strings", typeof(LocalizationService).Assembly);

    private string _language = "en";

    public event Action? LanguageChanged;

    public string CurrentLanguage => _language;

    public string this[string key]
    {
        get
        {
            var culture = _language == "pl" ? CultureInfo.GetCultureInfo("pl") : CultureInfo.GetCultureInfo("en");
            return _resources.GetString(key, culture) ?? key;
        }
    }

    public void SetLanguage(string language)
    {
        _language = language switch
        {
            "pl" or "en" => language,
            _ => DetectSystemLanguage()
        };
        LanguageChanged?.Invoke();
    }

    private static string DetectSystemLanguage()
    {
        var ui = CultureInfo.CurrentUICulture;
        return ui.TwoLetterISOLanguageName == "pl" ? "pl" : "en";
    }
}
