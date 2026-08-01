namespace GameLauncher.Core.Services.Interfaces;

public interface ILocalizationService
{
    string this[string key] { get; }
    string CurrentLanguage { get; }
    event Action? LanguageChanged;
    void SetLanguage(string language);
}
