using System.Runtime.InteropServices;
using System.Text;
using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>The serial port as far as the bootloader recovery needs it.</summary>
public interface ISerialLink
{
    /// <summary>Writes bytes to the board as they are, outside of GRBL's line handling.</summary>
    void WriteRaw(byte[] data);

    /// <summary>Sets the modem-control lines; <c>true</c> means asserted (<c>DtrEnable = true</c>).</summary>
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
/// <remarks>Raise <see cref="OnReceived"/> for every byte the port receives and run <see cref="RunAsync"/> for the life of the connection.</remarks>
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

    private readonly ISerialLink _link;
    private readonly Action<string> _notice;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly List<byte> _window = new();
    private bool _grblSeen;
    private bool _announced;
    private int _restarts;

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
