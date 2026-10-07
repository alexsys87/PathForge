using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PathForge.Core.Grbl;
using PathForge.Core.Localization;

namespace PathForge.App.Services;

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>How the control panel talks to the machine.</summary>
public enum MachineConnectionKind
{
    /// <summary>USB cable, COM port.</summary>
    Serial,

    /// <summary>Plain TCP stream (Grbl_Esp32 telnet, port 23).</summary>
    Telnet,

    /// <summary>WebSocket of the Grbl_Esp32 web interface (MKS DLC32: port 81, commands over HTTP).</summary>
    WebSocket,

    /// <summary>Serial-to-WebSocket bridge or FluidNC: commands are sent as WebSocket frames.</summary>
    WebSocketBridge,
}

/// <summary>Main window as the user left it: normal bounds (device-independent pixels), maximized, panel sizes and open tabs.</summary>
public sealed class WindowLayout
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }

    /// <summary>Width of the left panel (operations, layers, tools, machine, control).</summary>
    public double LeftPanelWidth { get; set; }

    /// <summary>Height of the bottom panel (G-code, messages).</summary>
    public double BottomPanelHeight { get; set; }

    public int LeftTab { get; set; }

    public int ViewTab { get; set; }

    public int BottomTab { get; set; }
}

/// <summary>Interface language, colour theme, machine connection and window layout, kept between runs in %AppData%\PathForge\settings.json.</summary>
public sealed class UiPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppLanguage Language { get; set; } = DefaultLanguage();

    public AppTheme Theme { get; set; } = AppTheme.Light;

    /// <summary>How the control panel connects to the machine.</summary>
    public MachineConnectionKind MachineConnection { get; set; }

    /// <summary>Address of the board for the network connection (192.168.4.1 is the MKS DLC32 in access point mode).</summary>
    public string MachineHost { get; set; } = "192.168.4.1";

    public int MachineNetworkPort { get; set; } = TelnetTransport.DefaultPort;

    /// <summary>Size and position of the main window and its panels at the last close (null before the first close).</summary>
    public WindowLayout? Window { get; set; }

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
