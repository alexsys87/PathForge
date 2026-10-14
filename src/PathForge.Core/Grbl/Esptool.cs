using System.Globalization;
using System.Text.RegularExpressions;
using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>
/// Version of esptool. Version 5 renamed the commands and options (<c>read_flash</c> became <c>read-flash</c>,
/// <c>no_reset</c> became <c>no-reset</c>); version 4 knows only the underscores.
/// </summary>
public readonly record struct EsptoolVersion(int Major, int Minor)
{
    private static readonly Regex Banner = new(
        @"^\s*esptool(?:\.py)?\s+v?(\d+)\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Bare = new(
        @"^\s*(\d+)\.(\d+)(?:\.\d+)*\s*$", RegexOptions.CultureInvariant);

    /// <summary>Version 5 spells commands and options with dashes (and warns about underscores); version 4 needs the underscores.</summary>
    public bool UsesDashes => Major >= 5;

    public override string ToString() => $"{Major}.{Minor}";

    /// <summary>
    /// Reads the version from the output of <c>esptool version</c>: the first line is "esptool v5.5.0" in version 5 and
    /// "esptool.py v4.8.1" in version 4, the second one is the bare number.
    /// </summary>
    public static bool TryParse(IEnumerable<string> lines, out EsptoolVersion version)
    {
        var all = lines.ToList();
        foreach (var pattern in new[] { Banner, Bare })
        {
            foreach (var line in all)
            {
                var match = pattern.Match(line);
                if (match.Success
                    && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                    && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
                {
                    version = new EsptoolVersion(major, minor);
                    return true;
                }
            }
        }

        version = default;
        return false;
    }
}

/// <summary>A way to start esptool: a program with the arguments that come before the esptool command (<c>python -m esptool</c>).</summary>
public sealed record EsptoolTool(string FileName, IReadOnlyList<string> PrefixArguments)
{
    /// <summary>Known once the tool has answered <c>version</c>.</summary>
    public EsptoolVersion Version { get; init; }

    public string Describe() => PrefixArguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', PrefixArguments)}";
}

/// <summary>The tool could not be started at all (no such file, not an executable, access denied).</summary>
public sealed class EsptoolNotStartedException : Exception
{
    public EsptoolNotStartedException(string tool, Exception? inner = null)
        : base($"Cannot start {tool}.", inner)
    {
    }
}

/// <summary>Starts esptool as a program of its own (it is GPL software that PathForge runs, never links).</summary>
public interface IEsptoolProcess
{
    /// <summary>
    /// Runs the tool and returns its exit code. <paramref name="onLine"/> gets every line of its output (standard output and
    /// standard error together) as it appears; the calls are never concurrent. When <paramref name="token"/> is cancelled the
    /// process (with its children) is killed and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    /// <exception cref="EsptoolNotStartedException">The tool cannot be started.</exception>
    Task<int> RunAsync(EsptoolTool tool, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken token);
}

/// <summary>Where esptool may be: a file the operator named, the folder of the program, the search path, a Python that has it.</summary>
public static class EsptoolLocator
{
    /// <summary>The ways to start esptool, the best first. Nothing is started here: each one is tried with <c>version</c>.</summary>
    public static IReadOnlyList<EsptoolTool> Candidates(string? explicitPath, string appDirectory, Func<string, bool> fileExists, bool windows)
    {
        var list = new List<EsptoolTool>();
        var exe = windows ? "esptool.exe" : "esptool";

        if (!string.IsNullOrWhiteSpace(explicitPath) && fileExists(explicitPath))
        {
            list.Add(new EsptoolTool(explicitPath, Array.Empty<string>()));
        }

        if (!string.IsNullOrWhiteSpace(appDirectory))
        {
            // The standalone build of esptool is a folder (esptool.exe and its libraries): next to the program, in "tools"
            // or in a folder of its own.
            foreach (var directory in new[] { appDirectory, Path.Combine(appDirectory, "tools"), Path.Combine(appDirectory, "esptool") })
            {
                var path = Path.Combine(directory, exe);
                if (fileExists(path))
                {
                    list.Add(new EsptoolTool(path, Array.Empty<string>()));
                }
            }
        }

        // The search path (pip puts esptool there); the system looks for esptool.exe by itself.
        list.Add(new EsptoolTool("esptool", Array.Empty<string>()));

        // A Python that has the module (this also covers pip installations that are not on the search path).
        list.Add(new EsptoolTool(windows ? "python" : "python3", new[] { "-m", "esptool" }));
        if (windows)
        {
            list.Add(new EsptoolTool("py", new[] { "-3", "-m", "esptool" }));
        }

        return list;
    }
}

/// <summary>The command lines of esptool for an ESP32 board, spelled for the version of the tool.</summary>
public sealed class EsptoolCommands
{
    private readonly EsptoolVersion _version;
    private readonly string _port;
    private readonly int _baud;

    public EsptoolCommands(EsptoolVersion version, string port, int baud)
    {
        _version = version;
        _port = port;
        _baud = baud;
    }

    /// <summary>
    /// Asks the chip for the manufacturer, the device and the size of its flash. The board stays in the bootloader afterwards,
    /// so that the command that follows finds it there.
    /// </summary>
    public IReadOnlyList<string> FlashId(bool noReset) =>
        Compose(noReset, afterNoReset: true, Name("flash_id"));

    /// <summary>Reads <paramref name="size"/> bytes of the flash from its start into <paramref name="file"/>; the board is restarted afterwards.</summary>
    public IReadOnlyList<string> ReadFlash(bool noReset, int size, string file) =>
        Compose(noReset, afterNoReset: false, Name("read_flash"), "0x0", $"0x{size:X}", file);

    /// <summary>
    /// Writes <paramref name="file"/> from the start of the flash and checks it; the board is restarted afterwards. The size of
    /// the flash is taken from the chip, so a file that does not fit is refused by esptool itself.
    /// </summary>
    public IReadOnlyList<string> WriteFlash(bool noReset, string file) =>
        Compose(noReset, afterNoReset: false, Name("write_flash"), Name("--flash_size"), "detect", "0x0", file);

    /// <summary>
    /// <paramref name="noReset"/> leaves the lines DTR and RTS alone, for a board that was put into the bootloader by hand;
    /// otherwise esptool resets the board into the bootloader itself, the way the firmware mode of PathForge does.
    /// </summary>
    private List<string> Compose(bool noReset, bool afterNoReset, params string[] command)
    {
        var args = new List<string> { "--chip", "esp32", "--port", _port, "--baud", _baud.ToString(CultureInfo.InvariantCulture) };
        if (noReset)
        {
            args.Add("--before");
            args.Add(Name("no_reset"));
        }

        if (afterNoReset)
        {
            args.Add("--after");
            args.Add(Name("no_reset"));
        }

        args.AddRange(command);
        return args;
    }

    /// <summary>The name in the spelling of the version: <c>read_flash</c> or <c>read-flash</c>.</summary>
    private string Name(string name) => _version.UsesDashes ? name.Replace('_', '-') : name;
}

/// <summary>Why an esptool run failed, as far as its output tells.</summary>
public enum EsptoolFailure
{
    None,

    /// <summary>The COM port is busy (another program holds it) or does not exist.</summary>
    PortUnavailable,

    /// <summary>esptool could not get an answer of the bootloader: no auto-reset, no power, the wrong boot mode.</summary>
    NoConnection,

    /// <summary>Another chip than the ESP32 answered.</summary>
    WrongChip,

    /// <summary>The image is larger than the flash.</summary>
    DoesNotFit,

    /// <summary>The chip stopped answering in the middle of the operation (cable, power, a speed that is too high).</summary>
    ChipStopped,

    /// <summary>What was written is not what the file holds.</summary>
    VerifyFailed,

    Other,
}

/// <summary>Reads what esptool prints: the progress, the size of the flash, the reason of a failure.</summary>
public static class EsptoolOutput
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Version 5 prints "Reading from 0x00061a80 ━━━━━━ 20.0% 390.62kB/1.91MB [0s]", version 4 "Writing at 0x00010000... (3 %)"
    // and "1048576 (12 %)": the last number before a percent sign is the progress.
    private static readonly Regex Percent = new(@"(?<![\d.])(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);

    private static readonly Regex FlashSize = new(@"Detected flash size:\s*(\d+)\s*(KB|MB)", Options);

    private static readonly Regex Fatal = new(@"A fatal error occurred:\s*(.+)", Options);

    /// <summary>Progress of the operation in percent, when the line is a progress line.</summary>
    public static bool TryParseProgress(string line, out double percent)
    {
        percent = 0;
        var matches = Percent.Matches(line);
        if (matches.Count == 0)
        {
            return false;
        }

        return double.TryParse(matches[^1].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)
               && percent is >= 0 and <= 100;
    }

    /// <summary>The size of the flash from the line "Detected flash size: 8MB" ("Unknown" gives <c>false</c>).</summary>
    public static bool TryParseFlashSize(string line, out int bytes)
    {
        bytes = 0;
        var match = FlashSize.Match(line);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return false;
        }

        var size = count * (match.Groups[2].Value.Equals("MB", StringComparison.OrdinalIgnoreCase) ? 1024 * 1024 : 1024);
        if (size is <= 0 or > int.MaxValue)
        {
            return false;
        }

        bytes = (int)size;
        return true;
    }

    /// <summary>Lines that carry nothing: the dots of "Connecting....", a bare progress bar.</summary>
    public static bool IsNoise(string line) =>
        line.All(c => c is '.' or '_' or '-' or ' ' or '\u2501' or '\u2500');

    /// <summary>The message of the last "A fatal error occurred:" line, or an empty string.</summary>
    public static string FatalMessage(IEnumerable<string> lines)
    {
        var message = "";
        foreach (var line in lines)
        {
            var match = Fatal.Match(line);
            if (match.Success)
            {
                message = match.Groups[1].Value.Trim();
            }
        }

        return message;
    }

    /// <summary>The reason of a failure, from the output of a run that ended with a non-zero exit code.</summary>
    public static EsptoolFailure Classify(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        if (Has(text, "could not open", "port is busy", "PermissionError", "Access is denied"))
        {
            return EsptoolFailure.PortUnavailable;
        }

        if (Has(text, "will not fit in"))
        {
            return EsptoolFailure.DoesNotFit;
        }

        if (Has(text, "This chip is"))
        {
            return EsptoolFailure.WrongChip;
        }

        if (Has(text, "MD5 of file does not match", "Verification failed"))
        {
            return EsptoolFailure.VerifyFailed;
        }

        if (Has(text, "Failed to connect", "No serial data received", "Wrong boot mode detected"))
        {
            return EsptoolFailure.NoConnection;
        }

        if (Has(text, "chip stopped responding", "Timed out", "Invalid head of packet", "Unexpected status"))
        {
            return EsptoolFailure.ChipStopped;
        }

        return EsptoolFailure.Other;
    }

    /// <summary>What the operator should know and do about a failure.</summary>
    public static string Describe(EsptoolFailure failure, string detail)
    {
        var text = failure switch
        {
            EsptoolFailure.PortUnavailable => Loc.T(
                "COM-порт занят или не существует. Закройте программы, которые держат порт (Flash Download Tool, терминал, Candle, LightBurn, другое окно PathForge), и проверьте кабель и номер порта.",
                "The COM port is busy or does not exist. Close the programs that hold the port (Flash Download Tool, a terminal, Candle, LightBurn, another PathForge window) and check the cable and the port number."),
            EsptoolFailure.NoConnection => Loc.T(
                "esptool не смог связаться с загрузчиком платы. Проверьте, что плата запитана (по инструкции производителя MKS DLC32 нужно питание 12–24 В: одного USB мало), выбран нужный порт и кабель исправен. Если на плате не работает автосброс, переведите её в загрузчик вручную (замкните GPIO0 на землю и нажмите Reset) и повторите. Можно также снизить скорость прошивки.",
                "esptool could not reach the bootloader of the board. Check that the board is powered (the manufacturer says the MKS DLC32 needs 12-24 V, USB alone is not enough), that the port is the right one and the cable is good. If the auto-reset of the board does not work, put it into the bootloader by hand (connect GPIO0 to ground and press Reset) and try again. A lower flashing speed may help too."),
            EsptoolFailure.WrongChip => Loc.T(
                "К порту подключена не ESP32 (esptool определил другой чип). Выберите порт платы MKS DLC32.",
                "The port belongs to another chip than the ESP32 (esptool found a different one). Choose the port of the MKS DLC32."),
            EsptoolFailure.DoesNotFit => Loc.T(
                "Образ не помещается во флеш-память платы.",
                "The image does not fit into the flash of the board."),
            EsptoolFailure.ChipStopped => Loc.T(
                "Связь с платой оборвалась во время операции. Проверьте кабель USB и питание платы, снизьте скорость прошивки и повторите операцию.",
                "The board stopped answering during the operation. Check the USB cable and the power of the board, lower the flashing speed and try again."),
            EsptoolFailure.VerifyFailed => Loc.T(
                "Проверка не прошла: данные во флеш-памяти не совпали с файлом. Повторите операцию; если ошибка повторится, снизьте скорость прошивки или смените кабель USB.",
                "The check failed: the data in the flash differ from the file. Try again; if the error comes back, lower the flashing speed or change the USB cable."),
            _ => Loc.T("esptool завершился с ошибкой.", "esptool failed."),
        };

        return string.IsNullOrWhiteSpace(detail) ? text : $"{text}\nesptool: {detail}";
    }

    private static bool Has(string text, params string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
