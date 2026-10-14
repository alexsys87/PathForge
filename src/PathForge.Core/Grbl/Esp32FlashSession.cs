using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>How an operation on the flash ended.</summary>
public enum FlashResult
{
    Done,
    Failed,
    Cancelled,
    ToolNotFound,
}

/// <summary>The end of an operation on the flash: what happened and what the operator should know about it.</summary>
/// <param name="Result">How it ended.</param>
/// <param name="Message">Text for the operator, ready to be shown.</param>
/// <param name="Failure">The reason, when it failed.</param>
/// <param name="WriteStarted">The flash of the board may have been changed: a write began (and did not end well).</param>
public sealed record FlashOutcome(
    FlashResult Result,
    string Message,
    EsptoolFailure Failure = EsptoolFailure.None,
    bool WriteStarted = false)
{
    public bool Success => Result == FlashResult.Done;
}

/// <summary>
/// Reads and writes the whole flash of an ESP32 board (MKS DLC32) with esptool: finds the tool, asks the chip for the size
/// of its flash, runs the operation and turns what the tool prints into progress and into a verdict for the operator.
/// The port must be free: nothing else may hold it while esptool works.
/// </summary>
public sealed class Esp32FlashSession
{
    /// <summary>
    /// Flash of the MKS DLC32: 8192 KBytes by the data of the manufacturer (an ESP32-WROOM-32U module). The size is asked
    /// of the chip all the same, because older boards and firmware files have 4 MB.
    /// </summary>
    public const int DefaultFlashBytes = 8 * 1024 * 1024;

    /// <summary>Speed of the port while flashing (the default of esptool); the firmware itself talks at 115200.</summary>
    public const int DefaultBaud = 460800;

    // Asking the chip for the size of its flash takes this part of the progress bar, the operation itself the rest.
    private const double InfoShare = 5;

    private readonly IEsptoolProcess _process;
    private readonly string _appDirectory;
    private readonly Action<string> _log;
    private readonly Action<double, string> _progress;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _windows;
    private string? _explicitToolPath;
    private EsptoolTool? _tool;

    /// <param name="process">How esptool is started.</param>
    /// <param name="appDirectory">Folder of the program: esptool may lie next to it.</param>
    /// <param name="log">Lines for the operator: what esptool prints and what the session itself has to say. Called from any thread.</param>
    /// <param name="progress">Progress of the operation in percent and a text for it. Called from any thread.</param>
    /// <param name="fileExists">Tells whether a file exists (the file system by default).</param>
    /// <param name="windows">Whether this is Windows (the operating system by default).</param>
    public Esp32FlashSession(
        IEsptoolProcess process,
        string appDirectory,
        Action<string> log,
        Action<double, string> progress,
        Func<string, bool>? fileExists = null,
        bool? windows = null)
    {
        _process = process;
        _appDirectory = appDirectory;
        _log = log;
        _progress = progress;
        _fileExists = fileExists ?? File.Exists;
        _windows = windows ?? OperatingSystem.IsWindows();
    }

    /// <summary>A file the operator named; it is tried before everything else.</summary>
    public string? ExplicitToolPath
    {
        get => _explicitToolPath;
        set
        {
            if (_explicitToolPath != value)
            {
                _explicitToolPath = value;
                _tool = null;
            }
        }
    }

    /// <summary>The tool found by <see cref="FindToolAsync"/>.</summary>
    public EsptoolTool? Tool => _tool;

    /// <summary>How long a way to start esptool may take to answer <c>version</c> before the next one is tried.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Tries the ways to start esptool one by one (<c>esptool version</c>) and keeps the first that works.</summary>
    public async Task<EsptoolTool?> FindToolAsync(CancellationToken token)
    {
        if (_tool is not null)
        {
            return _tool;
        }

        foreach (var candidate in EsptoolLocator.Candidates(_explicitToolPath, _appDirectory, _fileExists, _windows))
        {
            token.ThrowIfCancellationRequested();
            var lines = new List<string>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                var exitCode = await _process.RunAsync(candidate, new[] { "version" }, lines.Add, timeout.Token);
                if (exitCode == 0 && EsptoolVersion.TryParse(lines, out var version))
                {
                    _tool = candidate with { Version = version };
                    return _tool;
                }
            }
            catch (EsptoolNotStartedException)
            {
                // Not there: the next way.
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // It started, but never answered: the next way.
            }
        }

        return null;
    }

    /// <summary>Reads the whole flash of the board into <paramref name="outputPath"/>.</summary>
    public async Task<FlashOutcome> ReadAsync(string port, int baud, string outputPath, CancellationToken token)
    {
        try
        {
            var tool = await FindToolAsync(token);
            if (tool is null)
            {
                return ToolNotFound();
            }

            var commands = new EsptoolCommands(tool.Version, port, baud);
            var info = await RunStepAsync(tool, commands.FlashId, 0, InfoShare, ConnectingText, false, token);
            if (!info.Success)
            {
                return Failed(info);
            }

            var flashBytes = FlashBytesOf(info.Lines);
            var read = await RunStepAsync(
                tool, noReset => commands.ReadFlash(noReset, flashBytes, outputPath), InfoShare, 100, ReadingText, info.NoReset, token);
            if (!read.Success)
            {
                TryDelete(outputPath);
                return Failed(read);
            }

            // esptool compares the data with the checksum of the chip itself; the size of the file is checked here.
            var length = File.Exists(outputPath) ? new FileInfo(outputPath).Length : -1;
            if (length != flashBytes)
            {
                TryDelete(outputPath);
                return new FlashOutcome(
                    FlashResult.Failed,
                    Loc.T(
                        $"Файл получился неполным ({Math.Max(length, 0)} байт вместо {flashBytes}). Повторите считывание.",
                        $"The file is incomplete ({Math.Max(length, 0)} bytes instead of {flashBytes}). Read the flash again."));
            }

            var image = Esp32ImageInfo.InspectFile(outputPath);
            var note = image.Kind == Esp32ImageKind.FullFlash
                ? Loc.T("Загрузчик и таблица разделов в образе на месте.", "The bootloader and the partition table are in the image.")
                : Loc.T(
                    "Внимание: в образе нет загрузчика или таблицы разделов. Во флеш-памяти платы, видимо, нет рабочей прошивки.",
                    "Warning: the image has no bootloader or partition table. The flash of the board seems to hold no working firmware.");
            return new FlashOutcome(
                FlashResult.Done,
                Loc.T(
                    $"Прошивка считана: {Path.GetFileName(outputPath)} ({FormatSize(flashBytes)}). {note} Плата перезапущена.",
                    $"The firmware is read: {Path.GetFileName(outputPath)} ({FormatSize(flashBytes)}). {note} The board is restarted."));
        }
        catch (EsptoolNotStartedException)
        {
            // Found a moment ago and gone now (deleted, blocked by an antivirus).
            _tool = null;
            TryDelete(outputPath);
            return ToolNotFound();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TryDelete(outputPath);
            return new FlashOutcome(FlashResult.Cancelled, Loc.T("Считывание отменено.", "Reading cancelled."));
        }
    }

    /// <summary>
    /// Writes the whole image <paramref name="imagePath"/> to the board from address 0 and checks it. The image has to be
    /// judged before (<see cref="Esp32ImageInfo"/>): here it is only compared with the size of the flash.
    /// </summary>
    public async Task<FlashOutcome> WriteAsync(string port, int baud, string imagePath, CancellationToken token)
    {
        var writing = false;
        try
        {
            var tool = await FindToolAsync(token);
            if (tool is null)
            {
                return ToolNotFound();
            }

            var commands = new EsptoolCommands(tool.Version, port, baud);
            var info = await RunStepAsync(tool, commands.FlashId, 0, InfoShare, ConnectingText, false, token);
            if (!info.Success)
            {
                return Failed(info);
            }

            var flashBytes = FlashBytesOf(info.Lines);
            var length = new FileInfo(imagePath).Length;
            if (length > flashBytes)
            {
                return new FlashOutcome(
                    FlashResult.Failed,
                    Loc.T(
                        $"Файл ({FormatSize(length)}) больше флеш-памяти платы ({FormatSize(flashBytes)}): записывать его нельзя.",
                        $"The file ({FormatSize(length)}) is larger than the flash of the board ({FormatSize(flashBytes)}): it cannot be written."),
                    EsptoolFailure.DoesNotFit);
            }

            writing = true;
            var write = await RunStepAsync(
                tool, noReset => commands.WriteFlash(noReset, imagePath), InfoShare, 100, WritingText, info.NoReset, token);
            if (!write.Success)
            {
                // A board that was never reached has not been touched; anything else may have been erased already.
                return Failed(write) with
                {
                    WriteStarted = write.Failure is not (EsptoolFailure.NoConnection or EsptoolFailure.PortUnavailable or EsptoolFailure.WrongChip),
                };
            }

            return new FlashOutcome(
                FlashResult.Done,
                Loc.T(
                    "Прошивка записана и проверена: контрольная сумма совпала. Плата перезапущена и работает с новой прошивкой. Настройки GRBL, которые хранятся во флеш-памяти, могли измениться: проверьте их командой $$.",
                    "The firmware is written and checked: the checksum matches. The board is restarted and runs the new firmware. The GRBL settings kept in the flash may have changed: check them with the command $$."));
        }
        catch (EsptoolNotStartedException)
        {
            _tool = null;
            return ToolNotFound() with { WriteStarted = writing };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new FlashOutcome(
                FlashResult.Cancelled,
                writing
                    ? Loc.T(
                        "Запись прервана. Во флеш-памяти может остаться неполная прошивка, и плата может не запускаться. Подключитесь в режиме прошивки и запишите прошивку заново.",
                        "Writing interrupted. The flash may hold an incomplete firmware and the board may not start. Connect in the firmware mode and write the firmware again.")
                    : Loc.T("Запись отменена, плата не изменена.", "Writing cancelled, the board is unchanged."),
                WriteStarted: writing);
        }
    }

    private static string ConnectingText(double percent) =>
        Loc.T("Подключение к плате…", "Connecting to the board…");

    private static string ReadingText(double percent) =>
        Loc.T($"Считывание прошивки: {percent:0} %", $"Reading the firmware: {percent:0} %");

    private static string WritingText(double percent) =>
        Loc.T($"Запись прошивки: {percent:0} %", $"Writing the firmware: {percent:0} %");

    private static string FormatSize(long bytes)
    {
        var megabytes = bytes / (1024.0 * 1024.0);
        return Loc.T($"{megabytes:0.##} МБ", $"{megabytes:0.##} MB");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial file that cannot be removed is not worth a second error.
        }
    }

    private FlashOutcome ToolNotFound() => new(
        FlashResult.ToolNotFound,
        Loc.T(
            "esptool не найден. Установите его (pip install esptool или готовая сборка для Windows со страницы github.com/espressif/esptool/releases) и положите esptool.exe рядом с PathForge.exe, в папку tools, либо укажите путь к нему.",
            "esptool was not found. Install it (pip install esptool, or the ready build for Windows from github.com/espressif/esptool/releases) and put esptool.exe next to PathForge.exe, into the tools folder, or name its path."));

    private static FlashOutcome Failed(StepResult step) => new(
        FlashResult.Failed, EsptoolOutput.Describe(step.Failure, step.Detail), step.Failure);

    /// <summary>The size of the flash that the chip reported, or that of the MKS DLC32 when it did not.</summary>
    private int FlashBytesOf(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (EsptoolOutput.TryParseFlashSize(line, out var bytes))
            {
                _log(bytes == DefaultFlashBytes
                    ? Loc.T($"Флеш-память платы: {FormatSize(bytes)}.", $"Flash of the board: {FormatSize(bytes)}.")
                    : Loc.T(
                        $"Флеш-память платы: {FormatSize(bytes)} (у MKS DLC32 обычно {FormatSize(DefaultFlashBytes)}).",
                        $"Flash of the board: {FormatSize(bytes)} (the MKS DLC32 usually has {FormatSize(DefaultFlashBytes)})."));
                return bytes;
            }
        }

        _log(Loc.T(
            $"esptool не назвал размер флеш-памяти: считаю, что он {FormatSize(DefaultFlashBytes)}, как у MKS DLC32.",
            $"esptool did not tell the size of the flash: taking {FormatSize(DefaultFlashBytes)}, as in the MKS DLC32."));
        return DefaultFlashBytes;
    }

    /// <summary>
    /// Runs one esptool command and maps its progress onto <paramref name="from"/>..<paramref name="to"/> of the bar. When the
    /// board does not answer after the reset that esptool makes with DTR and RTS, it is tried once more without any reset: the
    /// board may have been put into the bootloader by hand, and a reset would only take it out again.
    /// </summary>
    private async Task<StepResult> RunStepAsync(
        EsptoolTool tool,
        Func<bool, IReadOnlyList<string>> command,
        double from,
        double to,
        Func<double, string> stage,
        bool noReset,
        CancellationToken token)
    {
        while (true)
        {
            var result = new StepResult { NoReset = noReset };
            var sawProgress = false;
            var lastStep = -1;

            void OnLine(string raw)
            {
                var line = raw.Trim();
                if (line.Length == 0 || EsptoolOutput.IsNoise(line))
                {
                    return;
                }

                result.Lines.Add(line);
                if (EsptoolOutput.TryParseProgress(line, out var percent))
                {
                    // A line for every block would flood the screen: the bar moves by half a percent at a time.
                    sawProgress = true;
                    var step = (int)(percent * 2);
                    if (step != lastStep)
                    {
                        lastStep = step;
                        _progress(from + ((to - from) * percent / 100), stage(percent));
                    }

                    return;
                }

                _log("esptool: " + line);
            }

            _progress(from, stage(0));
            var exitCode = await _process.RunAsync(tool, command(noReset), OnLine, token);
            if (exitCode == 0)
            {
                result.Success = true;
                return result;
            }

            result.Failure = EsptoolOutput.Classify(result.Lines);
            result.Detail = EsptoolOutput.FatalMessage(result.Lines);
            if (!noReset && !sawProgress && result.Failure == EsptoolFailure.NoConnection)
            {
                _log(Loc.T(
                    "Плата не ответила после сброса по DTR/RTS. Пробую подключиться без сброса: плата должна уже быть в загрузчике.",
                    "The board did not answer after the reset by DTR/RTS. Trying without a reset: the board has to be in the bootloader already."));
                noReset = true;
                continue;
            }

            return result;
        }
    }

    private sealed class StepResult
    {
        public bool Success { get; set; }

        /// <summary>The reset by DTR/RTS was left out (it did not work, or the board was in the bootloader already).</summary>
        public bool NoReset { get; set; }

        public EsptoolFailure Failure { get; set; }

        public string Detail { get; set; } = "";

        public List<string> Lines { get; } = new();
    }
}
