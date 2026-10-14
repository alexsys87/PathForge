using System.Globalization;

namespace Dlc32Flasher;

/// <summary>
/// Language of the texts of the flasher (messages, errors, the WPF control). Russian or English; by default it follows the
/// language of the user interface of Windows. A host program sets <see cref="Russian"/> to match its own language.
/// </summary>
public static class FlasherText
{
    public static bool Russian { get; set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Russian or the English text.</summary>
    public static string T(string russian, string english) => Russian ? russian : english;

    /// <summary>Size in megabytes or kilobytes, for messages.</summary>
    public static string Size(long bytes) => bytes >= 1024 * 1024
        ? T($"{bytes / (1024.0 * 1024.0):0.##} МБ", $"{bytes / (1024.0 * 1024.0):0.##} MB")
        : T($"{bytes / 1024.0:0.#} КБ", $"{bytes / 1024.0:0.#} KB");
}
