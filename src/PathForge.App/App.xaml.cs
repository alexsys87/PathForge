using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace PathForge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF bindings use en-US by default; use the OS culture so that "2,5" is accepted in Russia.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "PathForge — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
