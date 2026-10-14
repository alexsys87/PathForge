using System.Buffers.Binary;

namespace Dlc32Flasher;

/// <summary>
/// The serial protocol of the ROM bootloader of the ESP32 (the chip of the MKS DLC32), as published by Espressif in the
/// "Serial Protocol" chapter of the esptool documentation. Only the commands the ROM itself understands are used, so no
/// helper program (the esptool "stub") is uploaded into the chip. One instance talks to one chip; it is not thread-safe.
/// </summary>
public sealed class EspRomLoader
{
    /// <summary>The ROM bootloader of the ESP32 starts at this speed.</summary>
    public const int InitialBaud = 115200;

    /// <summary>Data in one FLASH_DATA packet: the block size of the ESP32 ROM.</summary>
    public const int FlashBlockSize = 0x400;

    /// <summary>Data in one READ_FLASH_SLOW answer: the ROM reads no more than this at a time.</summary>
    public const int ReadBlockSize = 64;

    internal const byte CmdFlashBegin = 0x02;
    internal const byte CmdFlashData = 0x03;
    internal const byte CmdSync = 0x08;
    internal const byte CmdWriteReg = 0x09;
    internal const byte CmdReadReg = 0x0A;
    internal const byte CmdSpiSetParams = 0x0B;
    internal const byte CmdSpiAttach = 0x0D;
    internal const byte CmdReadFlashSlow = 0x0E;
    internal const byte CmdChangeBaud = 0x0F;
    internal const byte CmdSpiFlashMd5 = 0x13;

    /// <summary>Register whose value tells the chip family; the ESP32 has <see cref="Esp32Magic"/> there.</summary>
    internal const uint ChipMagicAddress = 0x40001000;
    internal const uint Esp32Magic = 0x00F01D83;

    // SPI0 controller of the ESP32 (the one wired to the flash): used to send the JEDEC "read ID" command to the flash.
    internal const uint SpiBase = 0x3FF42000;
    internal const uint SpiCmd = SpiBase + 0x00;
    internal const uint SpiUsr = SpiBase + 0x1C;
    internal const uint SpiUsr2 = SpiBase + 0x24;
    internal const uint SpiMisoDlen = SpiBase + 0x2C;
    internal const uint SpiW0 = SpiBase + 0x80;
    internal const uint SpiCmdUsr = 1u << 18;

    // Clock calibration of the ROM, used to correct the speed set by CHANGE_BAUDRATE.
    internal const uint RtcCaliConfig = 0x3FF5F06C;
    internal const uint EfuseWord4 = 0x3FF5A000 + (4 * 4);

    private const byte ChecksumSeed = 0xEF;
    private const byte RomInvalidMessage = 0x05;
    private const int WriteAttempts = 3;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IFlasherPort _port;
    private readonly SlipReader _reader;
    private readonly Action<string> _log;

    public EspRomLoader(IFlasherPort port, Action<string>? log = null)
    {
        _port = port;
        _reader = new SlipReader(port);
        _log = log ?? (_ => { });
    }

    /// <summary>How the pauses of the reset sequences are made (replaced in tests).</summary>
    internal Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    // ---- Connection ------------------------------------------------------------------------------------

    /// <summary>
    /// Brings the chip into its ROM bootloader and synchronises with it at <see cref="InitialBaud"/>.
    /// With <paramref name="reset"/> the board is reset into the bootloader through DTR and RTS (the auto-reset circuit of
    /// the MKS DLC32); without it the board must already be in the bootloader (GPIO0 held low while Reset is pressed).
    /// </summary>
    /// <exception cref="FlasherException">No answer, or the chip started its firmware instead of the bootloader.</exception>
    public void Connect(bool reset, int attempts, CancellationToken token)
    {
        _port.BaudRate = InitialBaud;
        var bootLog = "";
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            _port.DiscardInput();
            _reader.Reset();
            if (reset)
            {
                // Every second attempt holds GPIO0 low a little longer: some boards need it.
                ResetIntoBootloader(TimeSpan.FromMilliseconds(attempt % 2 == 0 ? 50 : 550));
            }

            for (var i = 0; i < 5; i++)
            {
                if (TrySync(token))
                {
                    return;
                }

                Sleep(TimeSpan.FromMilliseconds(50));
            }

            if (_reader.Text.Length > 0)
            {
                bootLog = _reader.Text;
            }
        }

        if (bootLog.Contains("waiting for download", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlasherException(FlasherError.NoAnswer, FlasherText.T(
                "Плата в загрузчике (он напечатал «waiting for download»), но не отвечает на команды. Проверьте кабель USB и попробуйте снизить скорость.",
                "The board is in the bootloader (it printed \"waiting for download\") but does not answer commands. Check the USB cable and try a lower speed."));
        }

        if (bootLog.Contains("boot:0x", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlasherException(FlasherError.WrongBootMode, FlasherText.T(
                "Плата перезагрузилась, но запустила прошивку, а не загрузчик: автосброс не опустил GPIO0. Переведите плату в загрузчик вручную (замкните GPIO0/BOOT на землю и нажмите Reset) и повторите с флажком «Плата уже в загрузчике».",
                "The board restarted but ran its firmware instead of the bootloader: the auto-reset did not pull GPIO0 low. Put the board into the bootloader by hand (connect GPIO0/BOOT to ground and press Reset) and try again with \"The board is in the bootloader already\" checked."));
        }

        throw new FlasherException(FlasherError.NoAnswer, FlasherText.T(
            "Загрузчик ESP32 не отвечает. Проверьте, что выбран порт платы, плата запитана (MKS DLC32 нужно питание 12–24 В, одного USB может не хватить) и кабель передаёт данные. Если автосброс не работает, переведите плату в загрузчик вручную (замкните GPIO0 на землю и нажмите Reset).",
            "The ESP32 bootloader does not answer. Check that the port is the board's, that the board is powered (the MKS DLC32 needs 12-24 V, USB alone may not do) and that the cable carries data. If the auto-reset does not work, put the board into the bootloader by hand (connect GPIO0 to ground and press Reset)."));
    }

    /// <summary>
    /// The classic reset of ESP32 boards with a USB-serial chip: RTS pulls EN (reset) low and DTR pulls GPIO0 low, each through
    /// a transistor. EN goes high while GPIO0 is low, so the chip starts its ROM bootloader.
    /// </summary>
    public void ResetIntoBootloader(TimeSpan bootDelay)
    {
        _port.SetLines(dtr: false, rts: true); // GPIO0 high, EN low: the chip is held in reset
        Sleep(TimeSpan.FromMilliseconds(100));
        _port.SetLines(dtr: true, rts: false); // GPIO0 low, EN high: the chip starts and samples GPIO0
        Sleep(bootDelay);
        _port.SetLines(dtr: false, rts: false); // GPIO0 released
    }

    /// <summary>Restarts the chip into its firmware: EN pulsed low with GPIO0 high.</summary>
    public void HardReset()
    {
        _port.SetLines(dtr: false, rts: true);
        Sleep(TimeSpan.FromMilliseconds(100));
        _port.SetLines(dtr: false, rts: false);
    }

    private bool TrySync(CancellationToken token)
    {
        var data = new byte[36];
        data[0] = 0x07;
        data[1] = 0x07;
        data[2] = 0x12;
        data[3] = 0x20;
        data.AsSpan(4).Fill(0x55);
        try
        {
            Command(CmdSync, data, 0, SyncTimeout, token);
        }
        catch (FlasherException)
        {
            return false;
        }

        // The ROM answers a SYNC up to eight times: the extra answers must not be taken for answers to later commands.
        for (var i = 0; i < 7; i++)
        {
            if (_reader.ReadFrame(SyncTimeout, token) is null)
            {
                break;
            }
        }

        return true;
    }

    // ---- Chip and flash --------------------------------------------------------------------------------

    /// <summary>Checks that the chip is an ESP32 (and not an ESP32-S2, -S3, -C3… or an ESP8266).</summary>
    public void CheckChip(CancellationToken token)
    {
        var magic = ReadRegister(ChipMagicAddress, token);
        if (magic != Esp32Magic)
        {
            throw new FlasherException(FlasherError.WrongChip, FlasherText.T(
                $"К порту подключена не ESP32 (код чипа 0x{magic:X8}). Выберите порт платы MKS DLC32.",
                $"The port belongs to another chip than the ESP32 (chip code 0x{magic:X8}). Choose the port of the MKS DLC32."));
        }
    }

    /// <summary>Connects the SPI flash to the ROM (the ROM of the ESP32 needs it before any flash command).</summary>
    public void AttachFlash(CancellationToken token) =>
        CheckCommand(CmdSpiAttach, new byte[8], 0, DefaultTimeout, 0, token);

    /// <summary>The JEDEC ID of the flash chip: manufacturer in the low byte, then the type and the capacity code.</summary>
    public uint ReadFlashId(CancellationToken token)
    {
        // The SPI controller sends a "user" command: 8 command bits (0x9F, read ID) and reads 24 bits back.
        var oldUsr = ReadRegister(SpiUsr, token);
        var oldUsr2 = ReadRegister(SpiUsr2, token);
        WriteRegister(SpiMisoDlen, 24 - 1, token);
        WriteRegister(SpiUsr, (1u << 31) | (1u << 28), token); // USR_COMMAND | USR_MISO
        WriteRegister(SpiUsr2, (7u << 28) | 0x9F, token); // command length 8 bits, command RDID
        WriteRegister(SpiW0, 0, token);
        WriteRegister(SpiCmd, SpiCmdUsr, token);

        var done = false;
        for (var i = 0; i < 10 && !done; i++)
        {
            done = (ReadRegister(SpiCmd, token) & SpiCmdUsr) == 0;
        }

        if (!done)
        {
            throw new FlasherException(FlasherError.CommandFailed, FlasherText.T(
                "Флеш-память платы не ответила на запрос идентификатора.",
                "The flash of the board did not answer the ID request."));
        }

        var id = ReadRegister(SpiW0, token) & 0xFFFFFF;
        WriteRegister(SpiUsr, oldUsr, token);
        WriteRegister(SpiUsr2, oldUsr2, token);
        return id;
    }

    /// <summary>Size of the flash from its JEDEC ID (the capacity code in the high byte), or 0 when the code is unknown.</summary>
    public static long FlashSizeFromId(uint id)
    {
        var code = (id >> 16) & 0xFF;
        return code switch
        {
            >= 0x12 and <= 0x1C => 1L << (int)code,
            >= 0x32 and <= 0x3A => 1L << (int)(code - 0x20),
            0x20 => 64L << 20,
            0x21 => 128L << 20,
            0x22 => 256L << 20,
            _ => 0,
        };
    }

    /// <summary>Tells the ROM the size of the flash (otherwise it assumes a smaller one and refuses the addresses above).</summary>
    public void SetFlashSize(long size, CancellationToken token)
    {
        var data = new byte[24];
        Put(data, 0, 0); // id
        Put(data, 4, (uint)size); // total size
        Put(data, 8, 64 * 1024); // block
        Put(data, 12, 4 * 1024); // sector
        Put(data, 16, 256); // page
        Put(data, 20, 0xFFFF); // status mask
        CheckCommand(CmdSpiSetParams, data, 0, DefaultTimeout, 0, token);
    }

    /// <summary>
    /// Switches the bootloader and the port to <paramref name="baud"/>. The ROM of the ESP32 derives the speed from a clock it
    /// calibrated itself, which may be off by some percent: the speed asked for is corrected by that calibration.
    /// </summary>
    public void ChangeBaud(int baud, CancellationToken token)
    {
        if (baud == _port.BaudRate)
        {
            return;
        }

        var romBaud = (uint)baud;
        var calibration = (ReadRegister(RtcCaliConfig, token) >> 7) & 0x01FFFFFF;
        var clock8M = ReadRegister(EfuseWord4, token) & 0xFF;
        var romCrystal = calibration * 15625.0 * clock8M / 40;
        if (romCrystal > 1e6)
        {
            var crystal = romCrystal > 33e6 ? 40e6 : 26e6;
            var corrected = Math.Floor(baud * romCrystal / crystal);
            // A calibration far off would be a wrong reading rather than a clock: then the speed is sent as it is.
            if (corrected > baud * 0.8 && corrected < baud * 1.25)
            {
                romBaud = (uint)corrected;
            }
        }

        var data = new byte[8];
        Put(data, 0, romBaud);
        Put(data, 4, 0); // the ROM loader takes 0 as the old speed
        Command(CmdChangeBaud, data, 0, DefaultTimeout, token);
        _port.BaudRate = baud;
        Sleep(TimeSpan.FromMilliseconds(50));
        _port.DiscardInput();
        _reader.Reset();
    }

    // ---- Writing, verifying, reading -------------------------------------------------------------------

    /// <summary>
    /// Starts a write of <paramref name="size"/> bytes at <paramref name="offset"/>. The ROM erases the whole region now,
    /// which takes a while for megabytes.
    /// </summary>
    public int BeginFlash(uint offset, int size, CancellationToken token)
    {
        var blocks = (size + FlashBlockSize - 1) / FlashBlockSize;
        var data = new byte[16];
        Put(data, 0, (uint)size); // the size to erase
        Put(data, 4, (uint)blocks);
        Put(data, 8, FlashBlockSize);
        Put(data, 12, offset);
        CheckCommand(CmdFlashBegin, data, 0, PerMegabyte(40, size), 0, token);
        return blocks;
    }

    /// <summary>Sends one block of a write (padded with 0xFF to the block size); repeated up to three times on a failure.</summary>
    public void WriteBlock(ReadOnlySpan<byte> block, int sequence, CancellationToken token)
    {
        var payload = new byte[16 + FlashBlockSize];
        Put(payload, 0, FlashBlockSize);
        Put(payload, 4, (uint)sequence);
        payload.AsSpan(16).Fill(0xFF);
        block.CopyTo(payload.AsSpan(16));
        var checksum = Checksum(payload.AsSpan(16));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                CheckCommand(CmdFlashData, payload, checksum, PerMegabyte(40, FlashBlockSize), 0, token);
                return;
            }
            catch (FlasherException ex) when (attempt < WriteAttempts && ex.Error is FlasherError.Timeout or FlasherError.CommandFailed)
            {
                _log(FlasherText.T(
                    $"Блок {sequence} не записался ({ex.Message}), повтор…",
                    $"Block {sequence} was not written ({ex.Message}), retrying…"));
                _port.DiscardInput();
                _reader.Reset();
            }
        }
    }

    /// <summary>MD5 of a region of the flash, as the chip computes it (lower-case hex).</summary>
    public string FlashMd5(uint offset, int size, CancellationToken token)
    {
        var data = new byte[16];
        Put(data, 0, offset);
        Put(data, 4, (uint)size);
        var (_, digest) = CheckCommand(CmdSpiFlashMd5, data, 0, PerMegabyte(8, size), 32, token);
        // The ROM sends the digest as 32 hex characters.
        return System.Text.Encoding.ASCII.GetString(digest).ToLowerInvariant();
    }

    /// <summary>Reads up to 64 bytes of the flash (the only read the ROM has; slow for whole megabytes).</summary>
    public byte[] ReadBlock(uint offset, int length, CancellationToken token)
    {
        if (length is <= 0 or > ReadBlockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var data = new byte[8];
        Put(data, 0, offset);
        Put(data, 4, (uint)length);
        var (_, block) = CheckCommand(CmdReadFlashSlow, data, 0, DefaultTimeout, ReadBlockSize, token);
        return block[..length];
    }

    // ---- Registers -------------------------------------------------------------------------------------

    public uint ReadRegister(uint address, CancellationToken token)
    {
        var data = new byte[4];
        Put(data, 0, address);
        return CheckCommand(CmdReadReg, data, 0, DefaultTimeout, 0, token).Value;
    }

    public void WriteRegister(uint address, uint value, CancellationToken token)
    {
        var data = new byte[16];
        Put(data, 0, address);
        Put(data, 4, value);
        Put(data, 8, 0xFFFFFFFF); // mask
        Put(data, 12, 0); // delay
        CheckCommand(CmdWriteReg, data, 0, DefaultTimeout, 0, token);
    }

    // ---- Packets ---------------------------------------------------------------------------------------

    /// <summary>The checksum of the data of FLASH_DATA: 0xEF XOR every byte.</summary>
    internal static uint Checksum(ReadOnlySpan<byte> data)
    {
        byte state = ChecksumSeed;
        foreach (var b in data)
        {
            state ^= b;
        }

        return state;
    }

    /// <summary>Sends a command and returns its answer, then checks the status bytes that follow <paramref name="dataLength"/> bytes of data.</summary>
    private (uint Value, byte[] Data) CheckCommand(
        byte command, byte[] data, uint checksum, TimeSpan timeout, int dataLength, CancellationToken token)
    {
        var (value, payload) = Command(command, data, checksum, timeout, token);
        // The ROM of the ESP32 sends four status bytes (status, error, two reserved); only the first two mean something.
        if (payload.Length < dataLength + 2)
        {
            if (payload.Length >= 2 && payload[0] != 0)
            {
                throw Refused(command, payload[1]);
            }

            throw new FlasherException(FlasherError.CommandFailed, FlasherText.T(
                $"Неполный ответ загрузчика на команду 0x{command:X2}.",
                $"Incomplete answer of the bootloader to command 0x{command:X2}."));
        }

        if (payload[dataLength] != 0)
        {
            throw Refused(command, payload[dataLength + 1]);
        }

        return (value, payload[..dataLength]);
    }

    /// <summary>Sends a command and waits for the answer with the same command code.</summary>
    internal (uint Value, byte[] Data) Command(byte command, byte[] data, uint checksum, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var packet = new byte[8 + data.Length];
        packet[0] = 0x00; // request
        packet[1] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), checksum);
        data.CopyTo(packet.AsSpan(8));
        try
        {
            _port.Write(Slip.Encode(packet));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new FlasherException(FlasherError.Timeout, LostText(), ex);
        }

        var deadline = DateTime.UtcNow + timeout;
        for (var frames = 0; frames < 100; frames++)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                break;
            }

            byte[]? frame;
            try
            {
                frame = _reader.ReadFrame(left, token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new FlasherException(FlasherError.Timeout, LostText(), ex);
            }

            if (frame is null)
            {
                break;
            }

            if (frame.Length < 8 || frame[0] != 0x01)
            {
                continue;
            }

            var value = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4));
            var payload = frame[8..];
            if (frame[1] == command)
            {
                return (value, payload);
            }

            if (payload.Length >= 2 && payload[0] != 0 && payload[1] == RomInvalidMessage)
            {
                throw new FlasherException(FlasherError.CommandFailed, FlasherText.T(
                    $"Загрузчик не знает команду 0x{command:X2}.",
                    $"The bootloader does not know command 0x{command:X2}."));
            }
        }

        throw new FlasherException(FlasherError.Timeout, command == CmdSync
            ? FlasherText.T("Нет ответа на синхронизацию.", "No answer to the synchronisation.")
            : LostText());
    }

    private static string LostText() => FlasherText.T(
        "Плата перестала отвечать. Проверьте кабель USB и питание платы, снизьте скорость и повторите.",
        "The board stopped answering. Check the USB cable and the power of the board, lower the speed and try again.");

    private static FlasherException Refused(byte command, byte error)
    {
        var reason = error switch
        {
            0x01 => FlasherText.T("неверный параметр", "invalid parameter"),
            0x05 => FlasherText.T("неверный формат пакета", "invalid packet"),
            0x06 => FlasherText.T("команда не выполнена", "the command failed"),
            0x07 => FlasherText.T("ошибка контрольной суммы (помехи на линии)", "checksum error (noise on the line)"),
            0x08 => FlasherText.T("ошибка записи флеш-памяти", "flash write error"),
            0x09 => FlasherText.T("ошибка чтения флеш-памяти", "flash read error"),
            0x0A => FlasherText.T("неверная длина чтения", "wrong read length"),
            _ => $"0x{error:X2}",
        };
        return new FlasherException(FlasherError.CommandFailed, FlasherText.T(
            $"Загрузчик отклонил команду 0x{command:X2}: {reason}.",
            $"The bootloader refused command 0x{command:X2}: {reason}."));
    }

    private static TimeSpan PerMegabyte(double seconds, long bytes)
    {
        var timeout = TimeSpan.FromSeconds(seconds * bytes / (1024.0 * 1024.0));
        return timeout < DefaultTimeout ? DefaultTimeout : timeout;
    }

    private static void Put(byte[] buffer, int at, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at), value);
}
