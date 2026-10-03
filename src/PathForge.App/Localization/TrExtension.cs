using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using PathForge.Core.Localization;

namespace PathForge.App.Localization;

/// <summary>Bindable view of <see cref="Loc.Language"/>: bindings to it refresh when the language changes.</summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private LocalizationSource()
    {
        Loc.LanguageChanged += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    public static LocalizationSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppLanguage Language => Loc.Language;
}

/// <summary>
/// Text in XAML that follows the interface language:
/// <c>Text="{l:Tr 'Глубина, мм', En='Depth, mm'}"</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string ru)
    {
        Ru = ru;
    }

    [ConstructorArgument("ru")]
    public string Ru { get; set; } = "";

    public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(LocalizationSource.Language))
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = new PickConverter(Ru, En),
        };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class PickConverter : IValueConverter
    {
        private readonly string _ru;
        private readonly string _en;

        public PickConverter(string ru, string en)
        {
            _ru = ru;
            _en = en;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is AppLanguage.English && _en.Length > 0 ? _en : _ru;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
