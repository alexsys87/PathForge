using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using PathForge.App.Services;
using PathForge.Core.Localization;

namespace PathForge.App;

public partial class App : Application
{
    /// <summary>Interface language and colour theme of this run.</summary>
    public static IAppearanceService Appearance { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF bindings use en-US by default; use the OS culture so that "2,5" is accepted in Russia.
        // Number formats follow the OS, independently of the interface language.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        // Language and theme are applied before the main window is created.
        var preferences = UiPreferences.Load();
        Loc.Language = preferences.Language;
        WpfAppearanceService.ApplyTheme(preferences.Theme);
        Appearance = new WpfAppearanceService(preferences);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, Loc.T("PathForge — ошибка", "PathForge — error"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
