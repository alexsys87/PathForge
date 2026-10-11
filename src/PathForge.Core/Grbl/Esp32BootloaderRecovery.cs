using System.Runtime.InteropServices;
using System.Text;
using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>The serial port as far as the bootloader recovery needs it.</summary>
public interface ISerialLink
{
    /// <summary>Writes bytes to the board as they are, outside of GRBL's line handling.</summary>
    void WriteRaw(byte[] data);

    /// <summary>
    /// Sets the modem-control lines; <c>true</c> means asserted (<c>DtrEnable = true</c>). DTR is applied before RTS, the
    /// order esptool uses: when the chip is let out of reset (RTS released) while DTR is asserted, the capacitor on EN
    /// keeps it in reset a moment longer, which is what lets GPIO0 be pulled low in time to start the bootloader. An
    /// implementation that sets the lines in the other order, or one at a time with a long gap, breaks the firmware mode.
    /// </summary>
    void SetModemLines(bool dtr, bool rts);

    /// <summary>Forgets the received text that has not ended with a line break yet (the binary rest of the bootloader's answers).</summary>
    void DiscardReceivedText();
}

/// <summary>
/// Gets an ESP32 board (MKS DLC32) out of its ROM bootloader. The USB-serial driver may hold DTR when the board is
/// powered on, long before any program opens the port; DTR is wired to GPIO0, so the chip starts the bootloader instead
/// of the GRBL firmware and answers neither status polls nor G-code. A board that has not said a word of GRBL for
/// <see cref="SilenceMs"/> is told from a board that is merely off or slow by the bootloader's own answer to the esptool
/// SYNC command. It is then reset like esptool does after flashing (DTR low, a pulse on RTS), so that it boots the firmware.
/// A board that prints the bootloader banner while connected is reset the same way.
/// </summary>
/// <remarks>
/// <para>Raise <see cref="OnReceived"/> for every byte the port receives and run <see cref="RunAsync"/> for the life of the connection.</para>
/// <para>
/// The firmware mode is the opposite, for an operator who wants the bootloader to dump the flash or to update the firmware
/// with esptool or the Flash Download Tool: run <see cref="RunFirmwareModeAsync"/> instead of <see cref="RunAsync"/> (the two
/// work against each other). The board is reset the way esptool does before it talks to the chip, so that GPIO0 is low when
/// the chip starts, and it is then left in the bootloader.
/// </para>
/// </remarks>
public sealed class Esp32BootloaderRecovery
{
    /// <summary>How often the received data is looked at.</summary>
    public const int PollMs = 200;

    /// <summary>A running GRBL answers the status polls at once; a board silent for this long is asked whether it is the bootloader.</summary>
    public const int SilenceMs = 1500;

    public const int SyncAttempts = 5;
    public const int SyncAnswerMs = 200;

    /// <summary>How long RTS holds the chip in reset (esptool: 100 ms).</summary>
    public const int ResetPulseMs = 200;

    /// <summary>Time for the chip to boot after the reset, before it is judged by what it printed.</summary>
    public const int FirmwareStartMs = 3000;

    /// <summary>Restarts per connection, so that a board that keeps falling back into the bootloader is not reset forever.</summary>
    public const int MaxRestarts = 3;

    private const int WindowLimit = 4096;

    /// <summary>The GRBL real-time status query. A running GRBL answers it at once; the bootloader takes no notice of it.</summary>
    private static readonly byte[] StatusQuery = { (byte)'?' };

    private readonly ISerialLink _link;
    private readonly Action<string> _notice;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly List<byte> _window = new();
    private bool _grblSeen;
    private bool _announced;
    private int _restarts;
    private bool _firmwareModeReported;

    /// <param name="link">The port.</param>
    /// <param name="notice">Receives the messages for the operator.</param>
    /// <param name="delay">Waits for a time; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test replaces it.</param>
    public Esp32BootloaderRecovery(ISerialLink link, Action<string> notice, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _link = link;
        _notice = notice;
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
    }

    /// <summary>The board has spoken GRBL (a status report or the startup banner) since the connection was made.</summary>
    public bool GrblSeen
    {
        get
        {
            lock (_gate)
            {
                return _grblSeen;
            }
        }
    }

    /// <summary>Every chunk of bytes the port receives. May be called from any thread.</summary>
    public void OnReceived(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var b in data)
            {
                _window.Add(b);
            }

            if (_window.Count > WindowLimit)
            {
                _window.RemoveRange(0, _window.Count - WindowLimit);
            }

            var window = CollectionsMarshal.AsSpan(_window);
            if (!_grblSeen && Esp32Bootloader.IsGrblTraffic(window))
            {
                _grblSeen = true;
            }

            if (Esp32Bootloader.IsDownloadBanner(Encoding.ASCII.GetString(window)))
            {
                _announced = true;
            }
        }
    }

    /// <summary>Watches the board until <paramref name="token"/> is cancelled (the connection is closed).</summary>
    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            var waited = 0;
            var silenceChecked = false;
            while (true)
            {
                await _delay(TimeSpan.FromMilliseconds(PollMs), token);
                waited += PollMs;
                if (TakeAnnounced())
                {
                    // The bootloader printed its banner while the program was connected: no need to ask it.
                    await RecoverAsync(true, token);
                }
                else if (!silenceChecked && waited >= SilenceMs)
                {
                    silenceChecked = true;
                    if (!GrblSeen)
                    {
                        // The banner is printed once at power-on, before the program connects: ask the bootloader itself.
                        await RecoverAsync(false, token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection was closed.
        }
    }

    /// <summary>
    /// The firmware mode: puts the board into the ROM bootloader and then watches it until <paramref name="token"/> is
    /// cancelled (the connection is closed). Nothing restarts the board out of the bootloader: it stays there until the
    /// operator restarts it. DTR stays asserted (GPIO0 low), so a reset by the Reset button of the board enters the
    /// bootloader again, and the operator is told when it does. Run it instead of <see cref="RunAsync"/>.
    /// </summary>
    public async Task RunFirmwareModeAsync(CancellationToken token)
    {
        try
        {
            await EnterBootloaderAsync(token);
            while (true)
            {
                await _delay(TimeSpan.FromMilliseconds(PollMs), token);
                if (TakeAnnounced())
                {
                    // The ROM prints the banner in several pieces and the first one is enough to announce it: let the
                    // rest arrive, then forget all of it, or the tail would be announced as a second banner.
                    await _delay(TimeSpan.FromMilliseconds(PollMs), token);
                    ClearWindow();
                    ClearAnnounced();
                    _notice(_firmwareModeReported ? BootloaderAgainText() : BootloaderEnteredText());
                    _firmwareModeReported = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection was closed.
        }
    }

    /// <summary>Resets the board into the bootloader and tells the operator whether it got there.</summary>
    private async Task EnterBootloaderAsync(CancellationToken token)
    {
        _notice(Loc.T(
            "Режим прошивки: перезапускаю плату в загрузчик ESP32 (DTR удерживает GPIO0 низким, RTS подаёт сброс)…",
            "Firmware mode: restarting the board into the ESP32 bootloader (DTR holds GPIO0 low, RTS pulses the reset)…"));

        ClearWindow();
        ClearAnnounced();
        await ResetIntoBootloaderAsync(token);

        // At the speed of the ROM (115200) the bootloader prints its banner at once. At another speed the banner is
        // garbled, and a bootloader is told from a silent board by its answer to SYNC; but a firmware that starts instead
        // needs its time (FirmwareStartMs) before it can be told from a board that is merely silent, and no frame meant
        // for the bootloader may reach a GRBL that is starting.
        var waited = 0;
        var heardSomething = false;
        while (waited < FirmwareStartMs && !heardSomething)
        {
            await _delay(TimeSpan.FromMilliseconds(PollMs), token);
            waited += PollMs;
            heardSomething = TakeAnnounced() || Esp32Bootloader.IsGrblTraffic(WindowBytes());
        }

        if (heardSomething)
        {
            // The first piece of what the board prints is enough to judge; the rest is on its way and must not be
            // taken for a new banner afterwards.
            await _delay(TimeSpan.FromMilliseconds(PollMs), token);
        }

        var heard = TakeWindow();
        ClearAnnounced();
        var entered = Esp32Bootloader.IsDownloadBanner(Encoding.ASCII.GetString(heard));
        var grblRuns = !entered && Esp32Bootloader.IsGrblTraffic(heard);
        if (!entered && !grblRuns)
        {
            // Silent. GRBL is asked first, with a status query that does it no harm: a SYNC frame sent to a running GRBL
            // would stay in its line buffer and spoil the first command typed afterwards.
            _link.WriteRaw(StatusQuery);
            await _delay(TimeSpan.FromMilliseconds(SyncAnswerMs), token);
            grblRuns = Esp32Bootloader.IsGrblTraffic(WindowBytes());
            entered = !grblRuns && await IsBootloaderAnsweringAsync(token);
        }

        if (entered)
        {
            _firmwareModeReported = true;
            _notice(BootloaderEnteredText());
        }
        else if (grblRuns)
        {
            _notice(Loc.T(
                "Плата не вошла в загрузчик: она запустила GRBL. Возможно, линии DTR/RTS платы не подключены к GPIO0/EN. Нажмите на плате кнопку Reset: пока PathForge подключён в режиме прошивки, DTR удерживает GPIO0 низким. Если не поможет — замкните GPIO0 на землю (кнопка BOOT, если она есть) и нажмите Reset.",
                "The board did not enter the bootloader: it started GRBL. The DTR/RTS lines of the board may not be wired to GPIO0/EN. Press the Reset button on the board: while PathForge is connected in the firmware mode, DTR holds GPIO0 low. If that does not help, connect GPIO0 to ground (the BOOT button, if there is one) and press Reset."));
        }
        else
        {
            _notice(Loc.T(
                "Плата не ответила загрузчику ESP32. Проверьте порт и питание платы. Нажмите на плате кнопку Reset: пока PathForge подключён в режиме прошивки, DTR удерживает GPIO0 низким, и плата сообщит о входе в загрузчик.",
                "The board did not answer the ESP32 bootloader. Check the port and the power of the board. Press the Reset button on the board: while PathForge is connected in the firmware mode, DTR holds GPIO0 low, and the board reports that it has entered the bootloader."));
        }
    }

    private static string BootloaderEnteredText() => Loc.T(
        "Плата в загрузчике ESP32 (режим прошивки). Чтобы снять дамп или обновить прошивку, нажмите «Отключить» и запустите esptool или Flash Download Tool: загрузчик ждёт их, пока плату не перезапустят.",
        "The board is in the ESP32 bootloader (firmware mode). To dump or update the firmware, press Disconnect and run esptool or the Flash Download Tool: the bootloader waits for them until the board is restarted.");

    private static string BootloaderAgainText() => Loc.T(
        "Плата снова в загрузчике ESP32 (режим прошивки).",
        "The board is in the ESP32 bootloader again (firmware mode).");

    private async Task RecoverAsync(bool announced, CancellationToken token)
    {
        if (_restarts >= MaxRestarts)
        {
            return;
        }

        if (!announced && !await IsBootloaderAnsweringAsync(token))
        {
            return;
        }

        _restarts++;
        _notice(Loc.T(
            "Плата ждёт загрузки прошивки (загрузчик ESP32): обычно так бывает, когда драйвер CH340 держит линии DTR/RTS в момент включения платы. Перезапускаю плату, чтобы запустилась прошивка GRBL…",
            "The board is waiting for a firmware download (ESP32 bootloader): this usually happens when the CH340 driver holds the DTR/RTS lines as the board is powered on. Restarting it so that the GRBL firmware starts…"));

        ClearWindow();
        await ResetBoardAsync(token);
        await _delay(TimeSpan.FromMilliseconds(FirmwareStartMs), token);

        // Judge by what the board printed since the reset.
        var heard = TakeWindow();
        ClearAnnounced();
        var firmwareRuns = Esp32Bootloader.IsGrblTraffic(heard);
        if (Esp32Bootloader.IsDownloadBanner(Encoding.ASCII.GetString(heard)) ||
            (!firmwareRuns && await IsBootloaderAnsweringAsync(token)))
        {
            _notice(Loc.T(
                "Плата осталась в загрузчике: сброс по линии RTS не подействовал. Нажмите на плате кнопку Reset: пока PathForge подключён, линия DTR опущена, и плата запустится с прошивкой.",
                "The board is still in the bootloader: the reset by the RTS line had no effect. Press the Reset button on the board: while PathForge is connected, the DTR line is low and the board will start its firmware."));
        }
        else if (firmwareRuns)
        {
            _notice(Loc.T("Плата перезапущена, прошивка GRBL работает.", "The board has restarted and the GRBL firmware is running."));
        }
    }

    /// <summary>Sends the esptool SYNC command and reports whether the ESP32 ROM bootloader answered it.</summary>
    private async Task<bool> IsBootloaderAnsweringAsync(CancellationToken token)
    {
        var frame = Esp32Bootloader.SyncFrame();
        ClearWindow();
        try
        {
            for (var attempt = 0; attempt < SyncAttempts; attempt++)
            {
                if (Esp32Bootloader.IsGrblTraffic(WindowBytes()))
                {
                    // The firmware has started after all (a slow start): leave it alone.
                    return false;
                }

                _link.WriteRaw(frame);
                await _delay(TimeSpan.FromMilliseconds(SyncAnswerMs), token);
                if (Esp32Bootloader.IsSyncResponse(WindowBytes()))
                {
                    // Let the rest of its answers arrive, so that they are not taken for the start of the next line.
                    await _delay(TimeSpan.FromMilliseconds(SyncAnswerMs), token);
                    return true;
                }
            }

            return false;
        }
        finally
        {
            ClearWindow();
            _link.DiscardReceivedText();
        }
    }

    /// <summary>
    /// The hard reset of esptool: DTR low keeps GPIO0 high, so that the chip boots from flash; RTS pulls EN low for a
    /// moment, then lets go.
    /// </summary>
    private async Task ResetBoardAsync(CancellationToken token)
    {
        _link.SetModemLines(dtr: false, rts: true);
        try
        {
            await _delay(TimeSpan.FromMilliseconds(ResetPulseMs), token);
        }
        finally
        {
            _link.SetModemLines(dtr: false, rts: false);
        }
    }

    /// <summary>
    /// The hard reset of esptool before it talks to the chip: RTS pulls EN low for a moment; then DTR is asserted (GPIO0
    /// low) as RTS lets go, so that the chip starts the ROM bootloader. The lines change in this order on purpose (DTR
    /// first): the capacitor on EN keeps the chip in reset until GPIO0 is pulled low. DTR stays asserted afterwards, so a
    /// reset by the Reset button of the board enters the bootloader as well (esptool lets go of it, which makes no difference
    /// to a chip that is in the bootloader already).
    /// </summary>
    private async Task ResetIntoBootloaderAsync(CancellationToken token)
    {
        _link.SetModemLines(dtr: false, rts: true);
        try
        {
            await _delay(TimeSpan.FromMilliseconds(ResetPulseMs), token);
        }
        catch
        {
            // The connection is being closed: do not leave the chip in reset.
            _link.SetModemLines(dtr: false, rts: false);
            throw;
        }

        _link.SetModemLines(dtr: true, rts: false);
    }

    private byte[] WindowBytes()
    {
        lock (_gate)
        {
            return _window.ToArray();
        }
    }

    private byte[] TakeWindow()
    {
        lock (_gate)
        {
            var data = _window.ToArray();
            _window.Clear();
            return data;
        }
    }

    private void ClearWindow()
    {
        lock (_gate)
        {
            _window.Clear();
        }
    }

    private bool TakeAnnounced()
    {
        lock (_gate)
        {
            var announced = _announced;
            _announced = false;
            return announced;
        }
    }

    private void ClearAnnounced()
    {
        lock (_gate)
        {
            _announced = false;
        }
    }
}
