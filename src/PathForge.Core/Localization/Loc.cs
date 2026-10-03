namespace PathForge.Core.Localization;

/// <summary>Languages of the user interface and of the messages produced by the core.</summary>
public enum AppLanguage
{
    Russian,
    English,
}

/// <summary>
/// Current interface language. Texts are kept next to the code as Russian/English pairs:
/// <c>Loc.T("Глубина", "Depth")</c>. Values already stored in projects (operation and tool names)
/// keep the language they were created in.
/// </summary>
public static class Loc
{
    private static AppLanguage _language = AppLanguage.Russian;

    /// <summary>Raised after <see cref="Language"/> has changed.</summary>
    public static event Action? LanguageChanged;

    public static AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;
            LanguageChanged?.Invoke();
        }
    }

    public static bool IsEnglish => _language == AppLanguage.English;

    /// <summary>The text in the current language.</summary>
    public static string T(string russian, string english) => _language == AppLanguage.English ? english : russian;
}
