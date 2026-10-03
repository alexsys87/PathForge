using System.Windows;
using PathForge.Core.Localization;

namespace PathForge.App.Services;

/// <summary>
/// Applies the language (<see cref="Loc"/>) and swaps the colour dictionary of the application.
/// Every brush in the styles is a DynamicResource, so the whole window recolours at once.
/// </summary>
public sealed class WpfAppearanceService : IAppearanceService
{
    private readonly UiPreferences _preferences;

    public WpfAppearanceService(UiPreferences preferences)
    {
        _preferences = preferences;
    }

    public AppLanguage Language
    {
        get => Loc.Language;
        set
        {
            Loc.Language = value;
            _preferences.Language = value;
            _preferences.Save();
        }
    }

    public AppTheme Theme
    {
        get => _preferences.Theme;
        set
        {
            ApplyTheme(value);
            _preferences.Theme = value;
            _preferences.Save();
        }
    }

    /// <summary>Replaces the colour dictionary (the first merged dictionary of App.xaml).</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var colors = new ResourceDictionary
        {
            Source = new Uri(
                theme == AppTheme.Dark ? "/PathForge;component/Themes/Dark.xaml" : "/PathForge;component/Themes/Light.xaml",
                UriKind.Relative),
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = colors;
        }
        else
        {
            merged.Add(colors);
        }
    }
}
