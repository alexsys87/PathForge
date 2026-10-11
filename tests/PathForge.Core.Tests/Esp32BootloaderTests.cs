using System.Text;
using PathForge.Core.Grbl;

namespace PathForge.Core.Tests;

/// <summary>
/// An ESP32 board (MKS DLC32) behind a USB-serial chip: in the ROM bootloader, running the GRBL firmware or off, with the
/// usual auto-reset circuit on DTR/RTS.
/// </summary>
internal sealed class FakeEsp32 : ISerialLink
{
    public const string RomLog = "ets Jun  8 2016 00:22:57\r\n\r\nrst:0x1 (POWERON_RESET),boot:0x13 (SPI_FAST_FLASH_BOOT)\r\nconfigsip: 0, SPIWP:0xee\r\nentry 0x400805dc\r\n";

    public const string BootloaderText = "ets Jun  8 2016 00:22:57\r\n\r\nrst:0x1 (POWERON_RESET),boot:0x3 (DOWNLOAD_BOOT(UART0/UART1/SDIO_REI_REO_V2))\r\nwaiting for download\r\n";

    public const string FirmwareText = RomLog + "Grbl 1.3a ['$' for help]\r\n";

    public const string StatusReport = "<Idle|MPos:0.000,0.000,0.000|FS:0,0>\r\n";

    private static readonly byte[] SyncAnswer = { 0xC0, 0x01, 0x08, 0x04, 0x00, 0x07, 0x07, 0x12, 0x20, 0x00, 0x00, 0x00, 0x00, 0xC0 };

    private bool _enLow;
    private int? _grblStartsAt;
    private string? _bannerTail;
    private int _bannerTailAt;

    public enum State
    {
        Off,
        Bootloader,
        Firmware,
    }

    public State Mode { get; set; }

    /// <summary>DTR and RTS are wired to EN and GPIO0 (false: a board without the auto-reset circuit).</summary>
    public bool ResetWorks { get; set; } = true;

    /// <summary>The chip starts the bootloader after a reset whatever the lines say.</summary>
    public bool BootsIntoBootloader { get; set; }

    /// <summary>DTR is not wired to GPIO0 (RTS still resets the chip): a reset never starts the bootloader.</summary>
    public bool Gpio0NotWired { get; set; }

    /// <summary>The program talks at a speed other than the 115200 of the ROM: the banner arrives garbled, only SYNC tells the bootloader.</summary>
    public bool GarbledBanner { get; set; }

    /// <summary>The ROM prints the banner in two pieces, the second one this long (ms of the virtual clock) after the first; 0 is in one piece.</summary>
    public int BannerTailDelayMs { get; set; }

    /// <summary>A firmware that starts after a reset prints its first line only this long (ms of the virtual clock) after the ROM log; 0 is at once.</summary>
    public int SlowStartMs { get; set; }

    /// <summary>The virtual clock, ms (set by <see cref="Advance"/>).</summary>
    public int Now { get; private set; }

    public Esp32BootloaderRecovery? Recovery { get; set; }

    public List<byte[]> Written { get; } = new();

    public List<(bool Dtr, bool Rts)> Lines { get; } = new();

    public int Discards { get; private set; }

    /// <summary>The driver held DTR while the board was running (or powered it on again): the bootloader prints its banner.</summary>
    public void EnterBootloader()
    {
        Mode = State.Bootloader;
        SayBanner();
    }

    /// <summary>A board that was still starting says its first words.</summary>
    public void StartFirmware()
    {
        Mode = State.Firmware;
        Say(FirmwareText);
    }

    public void Say(string text) => Recovery!.OnReceived(Encoding.ASCII.GetBytes(text));

    /// <summary>The virtual time passes: a firmware that starts slowly prints its first line, the second piece of a banner arrives.</summary>
    public void Advance(int nowMs)
    {
        Now = nowMs;
        if (_grblStartsAt is { } start && nowMs >= start)
        {
            _grblStartsAt = null;
            Say("Grbl 1.3a ['$' for help]\r\n");
        }

        if (_bannerTail is { } tail && nowMs >= _bannerTailAt)
        {
            _bannerTail = null;
            Say(tail);
        }
    }

    /// <summary>What the ROM prints when it starts the bootloader (noise, when the speed of the port is not the one of the ROM).</summary>
    private void SayBanner()
    {
        if (GarbledBanner)
        {
            Recovery!.OnReceived(new byte[] { 0xFC, 0xE0, 0x80, 0xFE, 0x00, 0xF8, 0xE0 });
        }
        else if (BannerTailDelayMs > 0)
        {
            // The first piece is enough to announce the bootloader (it has DOWNLOAD_BOOT in it).
            var cut = BootloaderText.IndexOf("waiting", StringComparison.Ordinal);
            Say(BootloaderText[..cut]);
            _bannerTail = BootloaderText[cut..];
            _bannerTailAt = Now + BannerTailDelayMs;
        }
        else
        {
            Say(BootloaderText);
        }
    }

    public void WriteRaw(byte[] data)
    {
        Written.Add(data);

        // The bootloader answers the esptool SYNC command (a SLIP frame of the command 8), a firmware ignores it.
        if (Mode == State.Bootloader && data.Length > 3 && data[0] == 0xC0 && data[2] == 0x08)
        {
            Recovery!.OnReceived(SyncAnswer);
            Recovery.OnReceived(SyncAnswer);
        }

        // A running GRBL answers a status query (the real-time character ?) at once, the bootloader takes no notice of it.
        if (Mode == State.Firmware && data.Length == 1 && data[0] == (byte)'?')
        {
            Say(StatusReport);
        }
    }

    public void SetModemLines(bool dtr, bool rts)
    {
        Lines.Add((dtr, rts));
        if (!ResetWorks)
        {
            return;
        }

        // RTS alone pulls EN low (the chip is reset), DTR alone pulls GPIO0 low (the bootloader), both together do nothing.
        var enLow = rts && !dtr;
        var gpio0Low = dtr && !rts && !Gpio0NotWired;
        var released = _enLow && !enLow;
        _enLow = enLow;
        if (!released)
        {
            return;
        }

        if (gpio0Low || BootsIntoBootloader)
        {
            Mode = State.Bootloader;
            SayBanner();
        }
        else
        {
            Mode = State.Firmware;
            if (SlowStartMs > 0)
            {
                // The ROM log comes at once, the firmware says its first line later.
                Say(RomLog);
                _grblStartsAt = Now + SlowStartMs;
            }
            else
            {
                Say(FirmwareText);
            }
        }
    }

    public void DiscardReceivedText() => Discards++;
}

/// <summary>Runs the recovery against a <see cref="FakeEsp32"/> on a virtual clock: every wait ends at once and advances the clock.</summary>
internal sealed class BootBench : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly int _limit;
    private readonly bool _polled;
    private int _clock;

    /// <param name="state">What the board does at the start.</param>
    /// <param name="seconds">Length of the run on the virtual clock.</param>
    /// <param name="polled">The program polls the status all the time (a connection that talks GRBL); false in the firmware mode, where nothing is sent to the board.</param>
    public BootBench(FakeEsp32.State state, int seconds = 12, bool polled = true)
    {
        Board = new FakeEsp32 { Mode = state };
        _limit = seconds * 1000;
        _polled = polled;
        Recovery = new Esp32BootloaderRecovery(Board, Notices.Add, Wait);
        Board.Recovery = Recovery;
    }

    public FakeEsp32 Board { get; }

    public Esp32BootloaderRecovery Recovery { get; }

    /// <summary>The messages for the operator.</summary>
    public List<string> Notices { get; } = new();

    /// <summary>Raised at every step of the virtual clock, with the time in milliseconds.</summary>
    public event Action<int>? Tick;

    /// <summary>Runs until the virtual clock reaches the limit.</summary>
    public Task RunAsync() => Recovery.RunAsync(_cts.Token);

    /// <summary>The firmware mode instead of the recovery, until the virtual clock reaches the limit.</summary>
    public Task RunFirmwareModeAsync() => Recovery.RunFirmwareModeAsync(_cts.Token);

    public void Dispose() => _cts.Dispose();

    private Task Wait(TimeSpan time, CancellationToken token)
    {
        _clock += (int)time.TotalMilliseconds;
        Board.Advance(_clock);

        // A board that runs GRBL answers the status polls that the program sends all the time.
        if (_polled && Board.Mode == FakeEsp32.State.Firmware)
        {
            Board.Say(FakeEsp32.StatusReport);
        }

        Tick?.Invoke(_clock);
        if (_clock >= _limit)
        {
            _cts.Cancel();
        }

        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public class Esp32BootloaderTests
{
    [Fact]
    public void Sync_frame_is_the_one_esptool_sends()
    {
        var frame = Esp32Bootloader.SyncFrame();

        Assert.Equal(46, frame.Length);
        Assert.Equal(new byte[] { 0xC0, 0x00, 0x08, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x07, 0x12, 0x20 }, frame[..13]);
        Assert.Equal(Enumerable.Repeat((byte)0x55, 32).ToArray(), frame[13..45]);
        Assert.Equal((byte)0xC0, frame[45]);
    }

    [Fact]
    public void Sync_answer_of_the_rom_is_recognised()
    {
        byte[] answer = { 0xC0, 0x01, 0x08, 0x04, 0x00, 0x07, 0x07, 0x12, 0x20, 0x00, 0x00, 0x00, 0x00, 0xC0 };
        Assert.True(Esp32Bootloader.IsSyncResponse(answer));

        // Noise in front of it, and the answer cut short at the end, as a port delivers it.
        Assert.True(Esp32Bootloader.IsSyncResponse(new byte[] { 0x3F, 0x3F, 0xC0, 0x01, 0x08 }));

        // The request itself, a status report and a half of an answer are no answers.
        Assert.False(Esp32Bootloader.IsSyncResponse(Esp32Bootloader.SyncFrame()));
        Assert.False(Esp32Bootloader.IsSyncResponse(Encoding.ASCII.GetBytes(FakeEsp32.StatusReport)));
        Assert.False(Esp32Bootloader.IsSyncResponse(new byte[] { 0xC0, 0x01 }));
    }

    [Fact]
    public void Bootloader_banner_is_told_from_the_boot_log_of_the_firmware()
    {
        Assert.True(Esp32Bootloader.IsDownloadBanner(FakeEsp32.BootloaderText));
        Assert.True(Esp32Bootloader.IsDownloadBanner("rst:0x1 (POWERON_RESET),boot:0x4 (DOWNLOAD(USB/UART0))"));

        Assert.False(Esp32Bootloader.IsDownloadBanner(FakeEsp32.RomLog));
        Assert.False(Esp32Bootloader.IsDownloadBanner(FakeEsp32.FirmwareText));
        Assert.False(Esp32Bootloader.IsDownloadBanner("Grbl 1.1f ['$' for help]\r\n"));
    }

    [Fact]
    public void Grbl_traffic_is_told_from_the_bootloader()
    {
        Assert.True(Esp32Bootloader.IsGrblTraffic(Encoding.ASCII.GetBytes(FakeEsp32.StatusReport)));
        Assert.True(Esp32Bootloader.IsGrblTraffic(Encoding.ASCII.GetBytes(FakeEsp32.FirmwareText)));
        Assert.True(Esp32Bootloader.IsGrblTraffic("[MSG:INFO: Using machine:MKS DLC32]\r\n"u8));

        Assert.False(Esp32Bootloader.IsGrblTraffic(Encoding.ASCII.GetBytes(FakeEsp32.BootloaderText)));
        Assert.False(Esp32Bootloader.IsGrblTraffic(Encoding.ASCII.GetBytes(FakeEsp32.RomLog)));
        Assert.False(Esp32Bootloader.IsGrblTraffic(new byte[] { 0xC0, 0x01, 0x08, 0x3C, 0x20, 0x31, 0xC0 }));
    }

    [Fact]
    public async Task Board_waiting_in_the_bootloader_is_restarted_into_the_firmware()
    {
        // The driver held DTR when the board was powered on: the banner was printed before the program connected.
        using var bench = new BootBench(FakeEsp32.State.Bootloader);

        await bench.RunAsync();

        Assert.Equal(FakeEsp32.State.Firmware, bench.Board.Mode);
        Assert.True(bench.Recovery.GrblSeen);

        // The bootloader is asked once (it answers at once), then DTR stays low and RTS pulses.
        Assert.Single(bench.Board.Written);
        Assert.Equal(Esp32Bootloader.SyncFrame(), bench.Board.Written[0]);
        Assert.Equal(new[] { (false, true), (false, false) }, bench.Board.Lines.ToArray());

        // The binary answer of the bootloader must not end up in the first line of the firmware.
        Assert.True(bench.Board.Discards > 0);

        // The operator is told what happens, and that it worked.
        Assert.Equal(2, bench.Notices.Count);
        Assert.DoesNotContain("Reset", bench.Notices[1]);
    }

    [Fact]
    public async Task Board_that_runs_GRBL_is_left_alone()
    {
        using var bench = new BootBench(FakeEsp32.State.Firmware);

        await bench.RunAsync();

        Assert.True(bench.Recovery.GrblSeen);
        Assert.Empty(bench.Board.Written);
        Assert.Empty(bench.Board.Lines);
        Assert.Empty(bench.Notices);
    }

    [Fact]
    public async Task Silent_board_that_is_no_bootloader_is_asked_but_not_reset()
    {
        // Wrong port, board without power or a firmware that is still starting.
        using var bench = new BootBench(FakeEsp32.State.Off);

        await bench.RunAsync();

        Assert.Equal(Esp32BootloaderRecovery.SyncAttempts, bench.Board.Written.Count);
        Assert.Empty(bench.Board.Lines);
        Assert.Empty(bench.Notices);
        Assert.Equal(FakeEsp32.State.Off, bench.Board.Mode);
    }

    [Fact]
    public async Task Board_that_starts_slowly_is_not_asked_again_once_it_speaks()
    {
        using var bench = new BootBench(FakeEsp32.State.Off);
        bench.Tick += time =>
        {
            if (time == 2000)
            {
                bench.Board.StartFirmware();
            }
        };

        await bench.RunAsync();

        // The first two questions went to a board that was still silent; the third was not sent.
        Assert.Equal(2, bench.Board.Written.Count);
        Assert.Empty(bench.Board.Lines);
        Assert.Empty(bench.Notices);
        Assert.Equal(FakeEsp32.State.Firmware, bench.Board.Mode);
    }

    [Fact]
    public async Task Board_is_given_time_to_start_before_it_is_asked()
    {
        using var bench = new BootBench(FakeEsp32.State.Bootloader, seconds: 1);

        await bench.RunAsync();

        Assert.Empty(bench.Board.Written);
        Assert.Empty(bench.Board.Lines);
    }

    [Fact]
    public async Task Banner_printed_while_connected_restarts_the_board_without_asking()
    {
        using var bench = new BootBench(FakeEsp32.State.Firmware);
        bench.Tick += time =>
        {
            if (time == 5000)
            {
                bench.Board.EnterBootloader();
            }
        };

        await bench.RunAsync();

        Assert.Equal(FakeEsp32.State.Firmware, bench.Board.Mode);
        Assert.Empty(bench.Board.Written);
        Assert.Equal(new[] { (false, true), (false, false) }, bench.Board.Lines.ToArray());
        Assert.Equal(2, bench.Notices.Count);
    }

    [Fact]
    public async Task Reset_that_has_no_effect_is_reported()
    {
        // A board whose DTR/RTS are not wired to EN and GPIO0: only its Reset button helps.
        using var bench = new BootBench(FakeEsp32.State.Bootloader) { Board = { ResetWorks = false } };

        await bench.RunAsync();

        Assert.Equal(FakeEsp32.State.Bootloader, bench.Board.Mode);
        Assert.Equal(2, bench.Board.Lines.Count);

        // Asked before the reset, and once more to find out whether it helped.
        Assert.Equal(2, bench.Board.Written.Count);
        Assert.Equal(2, bench.Notices.Count);
        Assert.Contains("Reset", bench.Notices[1]);
    }

    [Fact]
    public async Task Board_that_falls_back_into_the_bootloader_is_reported_and_not_reset_again()
    {
        using var bench = new BootBench(FakeEsp32.State.Bootloader) { Board = { BootsIntoBootloader = true } };

        await bench.RunAsync();

        Assert.Equal(FakeEsp32.State.Bootloader, bench.Board.Mode);
        Assert.Equal(2, bench.Board.Lines.Count);
        Assert.Equal(2, bench.Notices.Count);
        Assert.Contains("Reset", bench.Notices[1]);
    }

    [Fact]
    public async Task Board_is_restarted_at_most_three_times_per_connection()
    {
        using var bench = new BootBench(FakeEsp32.State.Firmware, seconds: 25);
        bench.Tick += time =>
        {
            if (time % 5000 == 0 && time < 25000)
            {
                bench.Board.EnterBootloader();
            }
        };

        await bench.RunAsync();

        Assert.Equal(Esp32BootloaderRecovery.MaxRestarts * 2, bench.Board.Lines.Count);
        Assert.Equal(Esp32BootloaderRecovery.MaxRestarts * 2, bench.Notices.Count);
    }

    // ---- The firmware mode: the board is put into the bootloader on purpose -----------------------

    [Fact]
    public async Task Firmware_mode_resets_the_board_into_the_bootloader_and_keeps_DTR_asserted()
    {
        // A board that runs GRBL, and one that already waits in the bootloader, end up the same way.
        foreach (var state in new[] { FakeEsp32.State.Firmware, FakeEsp32.State.Bootloader })
        {
            using var bench = new BootBench(state, polled: false);

            await bench.RunFirmwareModeAsync();

            Assert.Equal(FakeEsp32.State.Bootloader, bench.Board.Mode);

            // The reset of esptool: RTS pulls EN low, then DTR (GPIO0 low) is asserted as RTS lets go, and stays asserted.
            Assert.Equal(new[] { (false, true), (true, false) }, bench.Board.Lines.ToArray());

            // The banner of the bootloader told it: the board was not asked anything.
            Assert.Empty(bench.Board.Written);

            // The operator is told what happens, and that it worked.
            Assert.Equal(2, bench.Notices.Count);
            Assert.Contains("esptool", bench.Notices[1]);
            Assert.DoesNotContain("Reset", bench.Notices[1]);
        }
    }

    [Fact]
    public async Task Firmware_mode_is_not_left_by_the_recovery()
    {
        // The recovery restarts a board out of the bootloader; in the firmware mode that must never happen.
        using var bench = new BootBench(FakeEsp32.State.Bootloader, seconds: 30, polled: false);

        await bench.RunFirmwareModeAsync();

        Assert.Equal(FakeEsp32.State.Bootloader, bench.Board.Mode);

        // One reset at the start, no restart afterwards, and nothing to report but the entry.
        Assert.Equal(2, bench.Board.Lines.Count);
        Assert.Equal(2, bench.Notices.Count);
    }

    [Fact]
    public async Task Firmware_mode_tells_a_bootloader_at_another_speed_by_its_answer()
    {
        // At a speed other than the 115200 of the ROM the banner is garbled: only the answer to SYNC tells.
        using var bench = new BootBench(FakeEsp32.State.Firmware, polled: false) { Board = { GarbledBanner = true } };

        await bench.RunFirmwareModeAsync();

        Assert.Equal(FakeEsp32.State.Bootloader, bench.Board.Mode);

        // GRBL was asked first (a status query, which the bootloader ignores); then the bootloader, which answered at once.
        Assert.Equal(2, bench.Board.Written.Count);
        Assert.Equal(new[] { (byte)'?' }, bench.Board.Written[0]);
        Assert.Equal(Esp32Bootloader.SyncFrame(), bench.Board.Written[1]);
        Assert.Equal(2, bench.Notices.Count);
        Assert.DoesNotContain("Reset", bench.Notices[1]);
    }

    [Fact]
    public async Task Firmware_mode_reports_a_board_that_keeps_running_GRBL_and_does_not_send_it_SYNC()
    {
        // DTR/RTS are not wired to GPIO0/EN: the reset does nothing and GRBL goes on.
        using var bench = new BootBench(FakeEsp32.State.Firmware, polled: false) { Board = { ResetWorks = false } };

        await bench.RunFirmwareModeAsync();

        Assert.Equal(FakeEsp32.State.Firmware, bench.Board.Mode);

        // GRBL answered the status query at once. The SYNC frame, which would stay in its line buffer, was never sent.
        Assert.Single(bench.Board.Written);
        Assert.Equal(new[] { (byte)'?' }, bench.Board.Written[0]);

        Assert.Equal(2, bench.Notices.Count);
        Assert.Contains("Reset", bench.Notices[1]);
    }

    [Fact]
    public async Task Firmware_mode_does_not_ask_a_board_that_starts_GRBL_after_the_reset()
    {
        // The reset works, but GPIO0 is not held low (DTR not wired): the firmware starts and says so.
        using var bench = new BootBench(FakeEsp32.State.Firmware, polled: false) { Board = { Gpio0NotWired = true } };

        await bench.RunFirmwareModeAsync();

        Assert.Equal(FakeEsp32.State.Firmware, bench.Board.Mode);
        Assert.Empty(bench.Board.Written);
        Assert.Equal(2, bench.Notices.Count);
        Assert.Contains("Reset", bench.Notices[1]);
    }

    [Fact]
    public async Task Firmware_mode_sends_nothing_to_a_GRBL_that_is_still_starting()
    {
        // The firmware needs two seconds to print its first line. A SYNC frame sent meanwhile would stay in the UART
        // buffer and end up in the first command line of GRBL: the board is given time to start before it is asked.
        using var bench = new BootBench(FakeEsp32.State.Firmware, polled: false)
        {
            Board = { Gpio0NotWired = true, SlowStartMs = 2000 },
        };

        await bench.RunFirmwareModeAsync();

        Assert.Equal(FakeEsp32.State.Firmware, bench.Board.Mode);
        Assert.Empty(bench.Board.Written);
        Assert.Equal(2, bench.Notices.Count);
        Assert.Contains("Reset", bench.Notices[1]);
    }

    [Fact]
    public async Task Firmware_mode_does_not_take_the_tail_of_a_banner_for_a_second_one()
    {
        // The ROM prints its banner in pieces, and the first one is enough to announce it. The tail comes after the
        // poll that saw the first piece, in the entry (250 ms: the poll is at 200) as well as after a reset by hand.
        using var bench = new BootBench(FakeEsp32.State.Firmware, polled: false) { Board = { BannerTailDelayMs = 250 } };
        bench.Tick += time =>
        {
            if (time == 6000)
            {
                // The operator presses Reset (DTR still holds GPIO0 low); the poll sees the first piece at once.
                bench.Board.BannerTailDelayMs = 150;
                bench.Board.EnterBootloader();
            }
        };

        await bench.RunFirmwareModeAsync();

        // The start, the entry and the reset by hand: one notice each, none for the tail of a banner.
        Assert.Equal(3, bench.Notices.Count);
    }

    [Fact]
    public async Task Firmware_mode_reports_a_silent_board_after_asking_both_GRBL_and_the_bootloader()
    {
        // Wrong port, or a board without power.
        using var bench = new BootBench(FakeEsp32.State.Off, polled: false) { Board = { ResetWorks = false } };

        await bench.RunFirmwareModeAsync();

        Assert.Equal(1 + Esp32BootloaderRecovery.SyncAttempts, bench.Board.Written.Count);
        Assert.Equal(new[] { (byte)'?' }, bench.Board.Written[0]);
        Assert.Equal(Esp32Bootloader.SyncFrame(), bench.Board.Written[1]);
        Assert.Equal(FakeEsp32.State.Off, bench.Board.Mode);
        Assert.Equal(2, bench.Notices.Count);
        Assert.Contains("Reset", bench.Notices[1]);
    }

    [Fact]
    public async Task Firmware_mode_notices_a_board_that_the_operator_resets_by_hand()
    {
        // The automatic reset does nothing, but DTR holds GPIO0 low: the Reset button of the board does the job.
        using var bench = new BootBench(FakeEsp32.State.Firmware, polled: false) { Board = { ResetWorks = false } };
        bench.Tick += time =>
        {
            if (time == 8000)
            {
                bench.Board.EnterBootloader();
            }
        };

        await bench.RunFirmwareModeAsync();

        Assert.Equal(FakeEsp32.State.Bootloader, bench.Board.Mode);
        Assert.Equal(3, bench.Notices.Count);
        Assert.Contains("Reset", bench.Notices[1]);
        Assert.Contains("esptool", bench.Notices[2]);
    }

    [Fact]
    public async Task Firmware_mode_reports_every_reset_by_hand_once()
    {
        using var bench = new BootBench(FakeEsp32.State.Firmware, polled: false);
        bench.Tick += time =>
        {
            if (time is 6000 or 9000)
            {
                bench.Board.EnterBootloader();
            }
        };

        await bench.RunFirmwareModeAsync();

        // The start, the entry, and one short notice for each reset by hand: the banner is not announced again and again.
        Assert.Equal(4, bench.Notices.Count);
        Assert.NotEqual(bench.Notices[1], bench.Notices[2]);
        Assert.Equal(bench.Notices[2], bench.Notices[3]);
    }

    [Fact]
    public async Task Firmware_mode_does_not_leave_the_chip_in_reset_when_the_connection_closes()
    {
        // The connection is closed while RTS holds the chip in reset: both lines are let go.
        using var bench = new BootBench(FakeEsp32.State.Firmware, seconds: 0, polled: false);

        await bench.RunFirmwareModeAsync();

        Assert.Equal(new[] { (false, true), (false, false) }, bench.Board.Lines.ToArray());
    }
}
