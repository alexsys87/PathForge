using PathForge.Core.Localization;

namespace PathForge.App.Services;

/// <summary>Interface language and colour theme, switched from the menu and remembered between runs.</summary>
public interface IAppearanceService
{
    AppLanguage Language { get; set; }

    AppTheme Theme { get; set; }
}
