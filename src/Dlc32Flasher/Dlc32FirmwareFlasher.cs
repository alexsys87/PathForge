using System.Security.Cryptography;

namespace Dlc32Flasher;

/// <summary>What the flasher is doing.</summary>
public enum FlashStage
{
    Connecting,
    Erasing,
    Writing,
    Verifying,
    Reading,
    Restarting,
}

/// <summary>Progress of an operation: the stage and how far it is (bytes, when the stage has a size).</summary>
public readonly record struct FlashProgress(FlashStage Stage, long Done, long Total)
{
    public double Percent => Total > 0 ? Math.Clamp(100.0 * Done / Total, 0, 100) : 0;

    /// <summary>Text for a status line.</summary>
    public string Text => Stage switch
    {
        FlashStage.Connecting => FlasherText.T("Подключение к плате…", "Connecting to the board…"),
        FlashStage.Erasing => FlasherText.T($"Стирание флеш-памяти ({FlasherText.Size(Total)})…", $"Erasing the flash ({FlasherText.Size(Total)})…"),
        FlashStage.Writing => FlasherText.T($"Запись: {Percent:0} %", $"Writing: {Percent:0} %"),
        FlashStage.Verifying => FlasherText.T("Проверка контрольной суммы…", "Checking the checksum…"),
        FlashStage.Reading => FlasherText.T($"Чтение: {Percent:0} %", $"Reading: {Percent:0} %"),
        _ => FlasherText.T("Перезапуск платы…", "Restarting the board…"),
    };
}

/// <summary>The chip as it answered: the JEDEC ID of its flash and the size of the flash.</summary>
public sealed record ChipInfo(uint FlashId, long FlashSize)
{
    public string Describe() => FlasherText.T(
        $"ESP32, флеш-память {FlasherText.Size(FlashSize)} (производитель 0x{FlashId & 0xFF:X2}, тип 0x{(FlashId >> 8) & 0xFF:X2}{(FlashId >> 16) & 0xFF:X2})",
        $"ESP32, flash {FlasherText.Size(FlashSize)} (manufacturer 0x{FlashId & 0xFF:X2}, type 0x{(FlashId >> 8) & 0xFF:X2}{(FlashId >> 16) & 0xFF:X2})");
}

/// <summary>How an operation ended.</summary>
public enum FlasherResult
{
    Done,
    Failed,
    Cancelled,
}

/// <summary>The end of an operation, with a text ready for the operator.</summary>
/// <param name="Result">How it ended.</param>
/// <param name="Message">What happened and, after a failure, what to do.</param>
/// <param name="FlashChanged">The flash was erased or written (and the operation did not end well): write the firmware again.</param>
/// <param name="Error">The reason of a failure.</param>
public sealed record FlasherReport(FlasherResult Result, string Message, bool FlashChanged = false, FlasherError? Error = null)
{
    public bool Success => Result == FlasherResult.Done;
}

/// <summary>Settings of one operation.</summary>
public sealed record Dlc32FlasherOptions
{
    /// <summary>Speed of the port after the connection (the bootloader always starts at 115200).</summary>
    public int Baud { get; init; } = Dlc32FirmwareFlasher.DefaultBaud;

    /// <summary>
    /// Reset the board into the bootloader through DTR and RTS (the auto-reset of the MKS DLC32). Turn off for a board that
    /// was put into the bootloader by hand (GPIO0 held low while Reset is pressed).
    /// </summary>
    public bool ResetIntoBootloader { get; init; } = true;

    /// <summary>Restart the board into its firmware at the end.</summary>
    public bool RestartAfter { get; init; } = true;

    /// <summary>How many times the reset and the synchronisation are tried.</summary>
    public int ConnectAttempts { get; init; } = 4;
}

/// <summary>
/// Writes and reads the firmware of an MKS DLC32 (an ESP32-WROOM-32 with 8 MB of flash behind a CH340) through its USB
/// port, without esptool: the board is reset into the ESP32 ROM bootloader and the whole flash image is written from
/// address 0 and checked by MD5, or the whole flash is read into a file. Works for other ESP32 boards with the usual
/// auto-reset circuit too. Every method runs on a pool thread and may be called from the UI thread.
/// </summary>
public sealed class Dlc32FirmwareFlasher
{
    /// <summary>Speed of the port while flashing.</summary>
    public const int DefaultBaud = 460800;

    /// <summary>Flash of the MKS DLC32 (taken when the flash chip does not tell its size).</summary>
    public const long Dlc32FlashSize = 8L * 1024 * 1024;

    // Progress is reported every this many bytes of data, so that the UI is not flooded.
    private const int ReportEvery = 16 * 1024;

    private readonly Action<string> _log;
    private readonly Func<string, IFlasherPort> _openPort;

    /// <param name="log">Lines for the operator (called from a pool thread).</param>
    /// <param name="openPort">Opens a port by name; a COM port of the computer by default.</param>
    public Dlc32FirmwareFlasher(Action<string>? log = null, Func<string, IFlasherPort>? openPort = null)
    {
        _log = log ?? (_ => { });
        _openPort = openPort ?? (name => new SerialFlasherPort(name));
    }

    /// <summary>Speeds the bootloader of the ESP32 works at with the CH340 of the MKS DLC32.</summary>
    public static IReadOnlyList<int> BaudRates { get; } = new[] { 115200, 230400, 460800, 921600 };

    /// <summary>How the pauses are made (replaced in tests).</summary>
    internal Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>Checks a firmware file before it is offered for writing.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    public static FirmwareImageInfo InspectFile(string path) => FirmwareImageInfo.Inspect(File.ReadAllBytes(path));

    /// <summary>Writes a whole flash image (a firmware file of the MKS DLC32 or a copy read before) from address 0.</summary>
    public Task<FlasherReport> WriteFileAsync(
        string portName, string imagePath, Dlc32FlasherOptions? options = null, IProgress<FlashProgress>? progress = null,
        CancellationToken token = default) =>
        Task.Run(() =>
        {
            byte[] image;
            try
            {
                image = File.ReadAllBytes(imagePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FlasherReport(FlasherResult.Failed, FlasherText.T(
                    $"Не удалось прочитать файл: {ex.Message}", $"Cannot read the file: {ex.Message}"), Error: FlasherError.BadImage);
            }

            return Write(portName, image, options ?? new Dlc32FlasherOptions(), progress, token);
        });

    /// <inheritdoc cref="WriteFileAsync"/>
    public Task<FlasherReport> WriteAsync(
        string portName, byte[] image, Dlc32FlasherOptions? options = null, IProgress<FlashProgress>? progress = null,
        CancellationToken token = default) =>
        Task.Run(() => Write(portName, image, options ?? new Dlc32FlasherOptions(), progress, token));

    /// <summary>
    /// Reads the whole flash into <paramref name="outputPath"/>: a copy of the firmware and of the settings kept in the flash.
    /// The ROM bootloader reads 64 bytes at a time, so 8 MB take several minutes.
    /// </summary>
    public Task<FlasherReport> ReadFileAsync(
        string portName, string outputPath, Dlc32FlasherOptions? options = null, IProgress<FlashProgress>? progress = null,
        CancellationToken token = default) =>
        Task.Run(() => Read(portName, outputPath, options ?? new Dlc32FlasherOptions(), progress, token));

    private FlasherReport Write(string portName, byte[] image, Dlc32FlasherOptions options, IProgress<FlashProgress>? progress, CancellationToken token)
    {
        var info = FirmwareImageInfo.Inspect(image);
        if (info.RefusalReason() is { } refusal)
        {
            return new FlasherReport(FlasherResult.Failed, refusal, Error: FlasherError.BadImage);
        }

        var changed = false;
        EspRomLoader? loader = null;
        try
        {
            using var port = _openPort(portName);
            loader = new EspRomLoader(port, _log) { Sleep = Sleep };
            var chip = Connect(loader, options, progress, token);
            if (image.Length > chip.FlashSize)
            {
                RestartQuietly(loader, options);
                return new FlasherReport(FlasherResult.Failed, FlasherText.T(
                    $"Файл ({FlasherText.Size(image.Length)}) больше флеш-памяти платы ({FlasherText.Size(chip.FlashSize)}): записывать его нельзя.",
                    $"The file ({FlasherText.Size(image.Length)}) is larger than the flash of the board ({FlasherText.Size(chip.FlashSize)}): it cannot be written."),
                    Error: FlasherError.DoesNotFit);
            }

            if (info.DeclaredFlashSize > chip.FlashSize)
            {
                _log(FlasherText.T(
                    $"Внимание: образ рассчитан на флеш-память {FlasherText.Size(info.DeclaredFlashSize)}, а у платы {FlasherText.Size(chip.FlashSize)}.",
                    $"Warning: the image is made for {FlasherText.Size(info.DeclaredFlashSize)} of flash, the board has {FlasherText.Size(chip.FlashSize)}."));
            }

            // Whole 32-bit words, as esptool sends them; the padding is 0xFF, the value of erased flash.
            var data = image;
            if (data.Length % 4 != 0)
            {
                data = new byte[(image.Length + 3) / 4 * 4];
                data.AsSpan().Fill(0xFF);
                image.CopyTo(data, 0);
            }

            progress?.Report(new FlashProgress(FlashStage.Erasing, 0, data.Length));
            _log(FlasherText.T(
                $"Стирание {FlasherText.Size(data.Length)}: загрузчик стирает всю область сразу, это займёт до минуты…",
                $"Erasing {FlasherText.Size(data.Length)}: the bootloader erases the whole region at once, this takes up to a minute…"));
            changed = true;
            var blocks = loader.BeginFlash(0, data.Length, token);

            var started = DateTime.UtcNow;
            for (var sequence = 0; sequence < blocks; sequence++)
            {
                var from = sequence * EspRomLoader.FlashBlockSize;
                var length = Math.Min(EspRomLoader.FlashBlockSize, data.Length - from);
                loader.WriteBlock(data.AsSpan(from, length), sequence, token);
                var done = from + length;
                if (done % ReportEvery == 0 || done == data.Length)
                {
                    progress?.Report(new FlashProgress(FlashStage.Writing, done, data.Length));
                }
            }

            _log(FlasherText.T(
                $"Записано {FlasherText.Size(data.Length)} за {(DateTime.UtcNow - started).TotalSeconds:0} с.",
                $"Written {FlasherText.Size(data.Length)} in {(DateTime.UtcNow - started).TotalSeconds:0} s."));

            progress?.Report(new FlashProgress(FlashStage.Verifying, 0, data.Length));
            var expected = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
            var actual = loader.FlashMd5(0, data.Length, token);
            if (actual != expected)
            {
                throw new FlasherException(FlasherError.VerifyFailed, FlasherText.T(
                    $"Проверка не прошла: MD5 флеш-памяти {actual}, а файла {expected}. Запишите прошивку ещё раз; если ошибка повторится, снизьте скорость или смените кабель USB.",
                    $"The check failed: the MD5 of the flash is {actual}, of the file {expected}. Write the firmware again; if the error comes back, lower the speed or change the USB cable."));
            }

            _log(FlasherText.T($"MD5 совпала: {actual}.", $"MD5 matches: {actual}."));
            if (options.RestartAfter)
            {
                progress?.Report(new FlashProgress(FlashStage.Restarting, 0, 0));
                loader.HardReset();
            }

            return new FlasherReport(FlasherResult.Done, FlasherText.T(
                "Прошивка записана и проверена: контрольная сумма совпала." + (options.RestartAfter ? " Плата перезапущена с новой прошивкой." : ""),
                "The firmware is written and checked: the checksum matches." + (options.RestartAfter ? " The board is restarted with the new firmware." : "")));
        }
        catch (FlasherException ex)
        {
            return Failed(ex, changed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new FlasherReport(
                FlasherResult.Cancelled,
                changed
                    ? FlasherText.T(
                        "Запись прервана. Во флеш-памяти неполная прошивка, и плата может не запускаться: запишите прошивку заново.",
                        "Writing interrupted. The flash holds an incomplete firmware and the board may not start: write the firmware again.")
                    : FlasherText.T("Запись отменена, плата не изменена.", "Writing cancelled, the board is unchanged."),
                changed);
        }
    }

    private FlasherReport Read(string portName, string outputPath, Dlc32FlasherOptions options, IProgress<FlashProgress>? progress, CancellationToken token)
    {
        try
        {
            using var port = _openPort(portName);
            var loader = new EspRomLoader(port, _log) { Sleep = Sleep };
            var chip = Connect(loader, options, progress, token);
            var size = (int)Math.Min(chip.FlashSize, int.MaxValue);
            var data = new byte[size];
            var started = DateTime.UtcNow;
            progress?.Report(new FlashProgress(FlashStage.Reading, 0, size));
            for (var offset = 0; offset < size; offset += EspRomLoader.ReadBlockSize)
            {
                var length = Math.Min(EspRomLoader.ReadBlockSize, size - offset);
                loader.ReadBlock((uint)offset, length, token).CopyTo(data, offset);
                var done = offset + length;
                if (done % ReportEvery == 0 || done == size)
                {
                    progress?.Report(new FlashProgress(FlashStage.Reading, done, size));
                }
            }

            progress?.Report(new FlashProgress(FlashStage.Verifying, 0, size));
            var expected = loader.FlashMd5(0, size, token);
            var actual = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
            if (actual != expected)
            {
                throw new FlasherException(FlasherError.VerifyFailed, FlasherText.T(
                    "Копия повреждена при передаче (MD5 не совпала с флеш-памятью). Повторите чтение на меньшей скорости.",
                    "The copy was damaged on the way (its MD5 differs from the flash). Read again at a lower speed."));
            }

            // A file that is half written is worse than none: the copy goes to a temporary file first.
            var temporary = outputPath + ".part";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, outputPath, overwrite: true);

            if (options.RestartAfter)
            {
                progress?.Report(new FlashProgress(FlashStage.Restarting, 0, 0));
                loader.HardReset();
            }

            var note = FirmwareImageInfo.Inspect(data).Kind == FirmwareImageKind.FullFlash
                ? FlasherText.T("Загрузчик и таблица разделов в копии на месте.", "The bootloader and the partition table are in the copy.")
                : FlasherText.T(
                    "Внимание: в копии нет загрузчика или таблицы разделов — во флеш-памяти, видимо, нет рабочей прошивки.",
                    "Warning: the copy has no bootloader or partition table: the flash seems to hold no working firmware.");
            return new FlasherReport(FlasherResult.Done, FlasherText.T(
                $"Прошивка считана за {(DateTime.UtcNow - started).TotalMinutes:0.#} мин: {Path.GetFileName(outputPath)} ({FlasherText.Size(size)}), MD5 совпала. {note}",
                $"The firmware is read in {(DateTime.UtcNow - started).TotalMinutes:0.#} min: {Path.GetFileName(outputPath)} ({FlasherText.Size(size)}), the MD5 matches. {note}"));
        }
        catch (FlasherException ex)
        {
            return Failed(ex, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FlasherReport(FlasherResult.Failed, FlasherText.T(
                $"Не удалось сохранить файл: {ex.Message}", $"Cannot save the file: {ex.Message}"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new FlasherReport(FlasherResult.Cancelled, FlasherText.T(
                "Чтение отменено, плата не изменена.", "Reading cancelled, the board is unchanged."));
        }
    }

    /// <summary>Bootloader, chip check, flash size and speed: everything before the flash itself.</summary>
    private ChipInfo Connect(EspRomLoader loader, Dlc32FlasherOptions options, IProgress<FlashProgress>? progress, CancellationToken token)
    {
        progress?.Report(new FlashProgress(FlashStage.Connecting, 0, 0));
        _log(options.ResetIntoBootloader
            ? FlasherText.T("Перезапуск платы в загрузчик ESP32…", "Restarting the board into the ESP32 bootloader…")
            : FlasherText.T("Подключение к загрузчику без сброса платы…", "Connecting to the bootloader without a reset…"));
        loader.Connect(options.ResetIntoBootloader, Math.Max(1, options.ConnectAttempts), token);
        loader.CheckChip(token);
        loader.AttachFlash(token);

        var id = loader.ReadFlashId(token);
        var size = EspRomLoader.FlashSizeFromId(id);
        if (size == 0)
        {
            _log(FlasherText.T(
                $"Размер флеш-памяти не распознан (ID 0x{id:X6}): считаю, что он {FlasherText.Size(Dlc32FlashSize)}, как у MKS DLC32.",
                $"The flash size is not recognised (ID 0x{id:X6}): taking {FlasherText.Size(Dlc32FlashSize)}, as in the MKS DLC32."));
            size = Dlc32FlashSize;
        }

        var chip = new ChipInfo(id, size);
        _log(chip.Describe());
        loader.SetFlashSize(size, token);

        if (options.Baud != EspRomLoader.InitialBaud)
        {
            loader.ChangeBaud(options.Baud, token);
            _log(FlasherText.T($"Скорость порта: {options.Baud} бод.", $"Port speed: {options.Baud} baud."));
        }

        return chip;
    }

    private static void RestartQuietly(EspRomLoader loader, Dlc32FlasherOptions options)
    {
        if (!options.RestartAfter)
        {
            return;
        }

        try
        {
            loader.HardReset();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The board stays in the bootloader: the Reset button brings it back.
        }
    }

    private static FlasherReport Failed(FlasherException ex, bool changed)
    {
        changed |= ex.FlashChanged;
        var message = changed
            ? ex.Message + " " + FlasherText.T(
                "Флеш-память уже была изменена: пока прошивка не записана заново, плата может не запускаться.",
                "The flash was changed already: the board may not start until the firmware is written again.")
            : ex.Message;
        return new FlasherReport(FlasherResult.Failed, message, changed, ex.Error);
    }
}
