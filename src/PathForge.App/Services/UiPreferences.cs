using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PathForge.Core.Localization;

namespace PathForge.App.Services;

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>Interface language and colour theme, kept between runs in %AppData%\PathForge\settings.json.</summary>
public sealed class UiPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppLanguage Language { get; set; } = DefaultLanguage();

    public AppTheme Theme { get; set; } = AppTheme.Light;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PathForge", "settings.json");

    /// <summary>Saved preferences, or defaults (language of the OS, light theme) when there are none.</summary>
    public static UiPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath), JsonOptions) ?? new UiPreferences();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged or unreadable file is not worth an error message: start with the defaults.
        }

        return new UiPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; failing to store them must not disturb the work.
        }
    }

    /// <summary>Russian for Russian-speaking systems, English otherwise.</summary>
    private static AppLanguage DefaultLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "be" or "kk"
            ? AppLanguage.Russian
            : AppLanguage.English;
}
