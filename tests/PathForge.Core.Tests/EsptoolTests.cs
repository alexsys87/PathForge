using PathForge.Core.Grbl;

namespace PathForge.Core.Tests;

/// <summary>
/// esptool is run as a program of its own: what PathForge relies on is its command line and the text it prints. The samples
/// below are real output of esptool 5.5.0 and 4.8.1.
/// </summary>
public sealed class EsptoolTests
{
    // `esptool version`
    private static readonly string[] Version5 = { "esptool v5.5.0", "5.5.0" };
    private static readonly string[] Version4 = { "esptool.py v4.8.1", "4.8.1" };

    // Run against a serial port that nobody answers on.
    private static readonly string[] SilentBoard5 =
    {
        "esptool v5.5.0",
        "Serial port /dev/pts/0:",
        "Connecting...NOTE: Failed to get VID/PID of a device on /dev/pts/0: Cannot resolve VID/PID. Using standard reset sequence.",
        ".....",
        "WARNING: Chip was NOT reset. Setting RTS/DTR lines is not supported for port '/dev/pts/0'.",
        "ERROR: A fatal error occurred: Failed to connect to ESP32: No serial data received.",
        "For troubleshooting steps visit: https://docs.espressif.com/projects/esptool/en/latest/troubleshooting.html",
    };

    private static readonly string[] SilentBoard4 =
    {
        "esptool.py v4.8.1",
        "Serial port /dev/pts/0",
        "Connecting...",
        "Failed to get PID of a device on /dev/pts/0, using standard reset sequence.",
        ".....",
        "A fatal error occurred: Failed to connect to ESP32: No serial data received.",
        "For troubleshooting steps visit: https://docs.espressif.com/projects/esptool/en/latest/troubleshooting.html",
    };

    private static readonly string[] MissingPort5 =
    {
        "esptool v5.5.0",
        "Serial port /dev/nonexistent:",
        "ERROR: A fatal error occurred: Could not open /dev/nonexistent, the port is busy or doesn't exist.",
        "([Errno 2] could not open port /dev/nonexistent: [Errno 2] No such file or directory: '/dev/nonexistent')",
        "Hint: Check if the port is correct and ESP connected",
    };

    private static readonly string[] BusyPortWindows4 =
    {
        "esptool.py v4.8.1",
        "A fatal error occurred: Could not open COM5, the port is busy or doesn't exist.",
        "(could not open port 'COM5': PermissionError(13, 'Access is denied.', None, 5))",
    };

    // ---- Version ---------------------------------------------------------------------------------

    [Fact]
    public void Version_of_esptool_5_is_read_from_its_banner()
    {
        Assert.True(EsptoolVersion.TryParse(Version5, out var version));
        Assert.Equal(new EsptoolVersion(5, 5), version);
        Assert.True(version.UsesDashes);
        Assert.Equal("5.5", version.ToString());
    }

    [Fact]
    public void Version_of_esptool_4_is_read_from_its_banner()
    {
        Assert.True(EsptoolVersion.TryParse(Version4, out var version));
        Assert.Equal(new EsptoolVersion(4, 8), version);
        Assert.False(version.UsesDashes);
    }

    [Fact]
    public void Version_falls_back_to_the_bare_number_and_ignores_other_output()
    {
        Assert.True(EsptoolVersion.TryParse(new[] { "4.7.0" }, out var version));
        Assert.Equal(new EsptoolVersion(4, 7), version);

        // A Python without the module, a program that is not esptool.
        Assert.False(EsptoolVersion.TryParse(new[] { "/usr/bin/python3: No module named esptool" }, out _));
        Assert.False(EsptoolVersion.TryParse(new[] { "Python 3.12.1" }, out _));
        Assert.False(EsptoolVersion.TryParse(Array.Empty<string>(), out _));
    }

    // ---- Command lines ---------------------------------------------------------------------------

    [Fact]
    public void Commands_of_esptool_5_are_spelled_with_dashes()
    {
        var commands = new EsptoolCommands(new EsptoolVersion(5, 5), "COM5", 460800);
        const string file = @"C:\Мои прошивки\dlc 32.bin";

        Assert.Equal(
            new[] { "--chip", "esp32", "--port", "COM5", "--baud", "460800", "--after", "no-reset", "flash-id" },
            commands.FlashId(false));
        Assert.Equal(
            new[] { "--chip", "esp32", "--port", "COM5", "--baud", "460800", "read-flash", "0x0", "0x800000", file },
            commands.ReadFlash(false, 8 * 1024 * 1024, file));
        Assert.Equal(
            new[] { "--chip", "esp32", "--port", "COM5", "--baud", "460800", "write-flash", "--flash-size", "detect", "0x0", file },
            commands.WriteFlash(false, file));
    }

    [Fact]
    public void Commands_of_esptool_4_are_spelled_with_underscores()
    {
        var commands = new EsptoolCommands(new EsptoolVersion(4, 8), "/dev/ttyUSB0", 115200);

        Assert.Equal(
            new[] { "--chip", "esp32", "--port", "/dev/ttyUSB0", "--baud", "115200", "--after", "no_reset", "flash_id" },
            commands.FlashId(false));
        Assert.Equal(
            new[] { "--chip", "esp32", "--port", "/dev/ttyUSB0", "--baud", "115200", "read_flash", "0x0", "0x400000", "dump.bin" },
            commands.ReadFlash(false, 4 * 1024 * 1024, "dump.bin"));
        Assert.Equal(
            new[] { "--chip", "esp32", "--port", "/dev/ttyUSB0", "--baud", "115200", "write_flash", "--flash_size", "detect", "0x0", "fw.bin" },
            commands.WriteFlash(false, "fw.bin"));
    }

    [Fact]
    public void Reset_is_left_out_on_request_in_either_spelling()
    {
        var five = new EsptoolCommands(new EsptoolVersion(5, 0), "COM3", 460800).ReadFlash(true, 0x800000, "a.bin");
        Assert.Equal(new[] { "--before", "no-reset" }, five.Skip(6).Take(2).ToArray());

        var four = new EsptoolCommands(new EsptoolVersion(4, 8), "COM3", 460800).FlashId(true);
        Assert.Equal(new[] { "--before", "no_reset", "--after", "no_reset" }, four.Skip(6).Take(4).ToArray());
    }

    [Fact]
    public void Options_come_before_the_command()
    {
        // esptool takes --port, --baud, --before and --after before the command, not after it.
        var args = new EsptoolCommands(new EsptoolVersion(5, 5), "COM5", 921600).WriteFlash(true, "x.bin").ToList();
        var command = args.IndexOf("write-flash");

        Assert.True(command > args.IndexOf("--port"));
        Assert.True(command > args.IndexOf("--baud"));
        Assert.True(command > args.IndexOf("--before"));
        Assert.True(args.IndexOf("--flash-size") > command);
    }

    // ---- What esptool prints ---------------------------------------------------------------------

    [Fact]
    public void Progress_of_esptool_5_is_read()
    {
        Assert.True(EsptoolOutput.TryParseProgress("Reading from 0x00061a80 ━━━━━━                          20.0% 390.62kB/1.91MB [0s] ", out var percent));
        Assert.Equal(20.0, percent);
        Assert.True(EsptoolOutput.TryParseProgress("Reading from 0x001e8480 ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ 100.0% 1.91MB/1.91MB [0s] ", out percent));
        Assert.Equal(100.0, percent);
    }

    [Fact]
    public void Progress_of_esptool_4_is_read()
    {
        Assert.True(EsptoolOutput.TryParseProgress("Writing at 0x00010000... (3 %)", out var percent));
        Assert.Equal(3.0, percent);
        Assert.True(EsptoolOutput.TryParseProgress("1048576 (12 %)", out percent));
        Assert.Equal(12.0, percent);
    }

    [Fact]
    public void Lines_without_progress_are_no_progress()
    {
        foreach (var line in new[]
                 {
                     "Hash of data verified.",
                     "Compressed 8327168 bytes to 1204331...",
                     "Wrote 8327168 bytes (1204331 compressed) at 0x00000000 in 31.2 seconds (effective 2135.0 kbit/s)...",
                     "Detected flash size: 8MB",
                     "Hard resetting via RTS pin...",
                     "",
                 })
        {
            Assert.False(EsptoolOutput.TryParseProgress(line, out _));
        }
    }

    [Fact]
    public void Size_of_the_flash_is_read_from_its_line()
    {
        Assert.True(EsptoolOutput.TryParseFlashSize("Detected flash size: 8MB", out var bytes));
        Assert.Equal(8 * 1024 * 1024, bytes);
        Assert.True(EsptoolOutput.TryParseFlashSize("Detected flash size: 4MB", out bytes));
        Assert.Equal(4 * 1024 * 1024, bytes);
        Assert.True(EsptoolOutput.TryParseFlashSize("Detected flash size: 16MB", out bytes));
        Assert.Equal(16 * 1024 * 1024, bytes);
        Assert.True(EsptoolOutput.TryParseFlashSize("Detected flash size: 512KB", out bytes));
        Assert.Equal(512 * 1024, bytes);

        Assert.False(EsptoolOutput.TryParseFlashSize("Detected flash size: Unknown", out _));
        Assert.False(EsptoolOutput.TryParseFlashSize("Manufacturer: ef", out _));
    }

    [Fact]
    public void Dots_and_bars_are_noise()
    {
        Assert.True(EsptoolOutput.IsNoise("....."));
        Assert.True(EsptoolOutput.IsNoise("._____....."));
        Assert.True(EsptoolOutput.IsNoise("━━━━━━━━"));
        Assert.False(EsptoolOutput.IsNoise("Connecting..."));
        Assert.False(EsptoolOutput.IsNoise("Chip is ESP32-D0WD"));
    }

    [Fact]
    public void Fatal_message_is_the_last_fatal_line_of_either_version()
    {
        Assert.Equal("Failed to connect to ESP32: No serial data received.", EsptoolOutput.FatalMessage(SilentBoard5));
        Assert.Equal("Failed to connect to ESP32: No serial data received.", EsptoolOutput.FatalMessage(SilentBoard4));
        Assert.Equal("", EsptoolOutput.FatalMessage(new[] { "Hash of data verified." }));
    }

    // ---- Why a run failed ------------------------------------------------------------------------

    [Fact]
    public void Port_that_is_missing_or_busy_is_told_from_the_output()
    {
        Assert.Equal(EsptoolFailure.PortUnavailable, EsptoolOutput.Classify(MissingPort5));
        Assert.Equal(EsptoolFailure.PortUnavailable, EsptoolOutput.Classify(BusyPortWindows4));
    }

    [Fact]
    public void Board_that_does_not_answer_is_told_from_the_output()
    {
        Assert.Equal(EsptoolFailure.NoConnection, EsptoolOutput.Classify(SilentBoard5));
        Assert.Equal(EsptoolFailure.NoConnection, EsptoolOutput.Classify(SilentBoard4));
        Assert.Equal(
            EsptoolFailure.NoConnection,
            EsptoolOutput.Classify(new[]
            {
                "A fatal error occurred: Failed to connect to ESP32: Wrong boot mode detected (0x13)! The chip needs to be in download mode.",
            }));
    }

    [Fact]
    public void Other_failures_are_told_from_the_output()
    {
        Assert.Equal(
            EsptoolFailure.DoesNotFit,
            EsptoolOutput.Classify(new[]
            {
                "A fatal error occurred: File 'big.bin' (length 9000000) at offset 0x00000000 will not fit in 4194304 bytes of flash. Change the flash_size argument or flashing address.",
            }));
        Assert.Equal(
            EsptoolFailure.WrongChip,
            EsptoolOutput.Classify(new[] { "A fatal error occurred: This chip is ESP32-S3, not ESP32. Wrong --chip argument?" }));
        Assert.Equal(
            EsptoolFailure.ChipStopped,
            EsptoolOutput.Classify(new[] { "A fatal error occurred: The chip stopped responding." }));
        Assert.Equal(
            EsptoolFailure.ChipStopped,
            EsptoolOutput.Classify(new[] { "A fatal error occurred: Timed out waiting for packet header" }));
        Assert.Equal(
            EsptoolFailure.VerifyFailed,
            EsptoolOutput.Classify(new[] { "A fatal error occurred: MD5 of file does not match data in flash!" }));
        Assert.Equal(
            EsptoolFailure.Other,
            EsptoolOutput.Classify(new[] { "A fatal error occurred: [Errno 13] Permission denied: 'dump.bin'" }));
    }

    [Fact]
    public void Description_of_a_failure_carries_the_line_of_esptool()
    {
        var text = EsptoolOutput.Describe(EsptoolFailure.Other, "[Errno 13] Permission denied: 'dump.bin'");
        Assert.Contains("Permission denied", text);

        // Without a detail there is nothing to append.
        Assert.DoesNotContain("esptool:", EsptoolOutput.Describe(EsptoolFailure.NoConnection, ""));
        foreach (var failure in Enum.GetValues<EsptoolFailure>().Where(f => f != EsptoolFailure.None))
        {
            Assert.True(EsptoolOutput.Describe(failure, "").Length > 10);
        }
    }

    // ---- Where esptool may be --------------------------------------------------------------------

    [Fact]
    public void Locator_tries_the_named_file_first_then_the_folder_of_the_program()
    {
        var app = Path.Combine("C:", "PathForge");
        var named = Path.Combine("D:", "tools", "esptool.exe");
        var next = Path.Combine(app, "esptool.exe");
        var inTools = Path.Combine(app, "tools", "esptool.exe");
        var existing = new HashSet<string> { named, next, inTools };

        var candidates = EsptoolLocator.Candidates(named, app, existing.Contains, windows: true);

        Assert.Equal(new[] { named, next, inTools }, candidates.Take(3).Select(c => c.FileName).ToArray());
    }

    [Fact]
    public void Locator_skips_files_that_do_not_exist_and_ends_with_the_search_path_and_python()
    {
        var windows = EsptoolLocator.Candidates("D:\\gone\\esptool.exe", "C:\\PathForge", _ => false, windows: true);
        Assert.Equal(
            new[] { "esptool", "python -m esptool", "py -3 -m esptool" },
            windows.Select(c => c.Describe()).ToArray());

        var linux = EsptoolLocator.Candidates(null, "/opt/pathforge", _ => false, windows: false);
        Assert.Equal(new[] { "esptool", "python3 -m esptool" }, linux.Select(c => c.Describe()).ToArray());
    }

    // ---- Firmware files --------------------------------------------------------------------------

    [Fact]
    public void Whole_flash_image_is_recognised_with_the_flash_size_it_asks_for()
    {
        foreach (var (code, expected) in new[] { (0x1, "2MB"), (0x2, "4MB"), (0x3, "8MB"), (0x4, "16MB") })
        {
            var head = FullImageHead((byte)((code << 4) | 0x0F));

            var info = Esp32ImageInfo.Inspect(head, 8_327_168);

            Assert.Equal(Esp32ImageKind.FullFlash, info.Kind);
            Assert.Equal(expected, info.DeclaredFlashSize);
            Assert.Equal(8_327_168, info.Length);
        }
    }

    [Fact]
    public void Image_that_starts_with_its_own_header_is_a_single_image()
    {
        // A bootloader or an application: it starts with the magic byte of an image and belongs at an offset of its own.
        var head = new byte[Esp32ImageInfo.HeadLength];
        head[0] = 0xE9;

        Assert.Equal(Esp32ImageKind.SingleImage, Esp32ImageInfo.Inspect(head, 1_200_000).Kind);
        Assert.Equal(Esp32ImageKind.SingleImage, Esp32ImageInfo.Inspect(new byte[] { 0xE9, 3, 2, 0x20 }, 4).Kind);
    }

    [Fact]
    public void Blank_flash_and_other_files_are_not_images()
    {
        var blank = new byte[Esp32ImageInfo.HeadLength];
        Array.Fill(blank, (byte)0xFF);
        Assert.Equal(Esp32ImageKind.Unrecognized, Esp32ImageInfo.Inspect(blank, 8 * 1024 * 1024).Kind);

        // The bootloader is there but the partition table is not.
        var half = FullImageHead(0x3F);
        half[Esp32ImageInfo.PartitionTableOffset] = 0;
        Assert.Equal(Esp32ImageKind.Unrecognized, Esp32ImageInfo.Inspect(half, 4_194_304).Kind);

        Assert.Equal(Esp32ImageKind.Unrecognized, Esp32ImageInfo.Inspect(ReadOnlySpan<byte>.Empty, 0).Kind);
        Assert.Equal(Esp32ImageKind.Unrecognized, Esp32ImageInfo.Inspect("G21 G90\n"u8, 8).Kind);
    }

    [Fact]
    public void Image_file_is_judged_by_its_first_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pathforge-image-{Guid.NewGuid():N}.bin");
        try
        {
            using (var stream = File.Create(path))
            {
                stream.Write(FullImageHead(0x2F));
                stream.SetLength(4 * 1024 * 1024);
            }

            var info = Esp32ImageInfo.InspectFile(path);

            Assert.Equal(Esp32ImageKind.FullFlash, info.Kind);
            Assert.Equal(4 * 1024 * 1024, info.Length);
            Assert.Equal("4MB", info.DeclaredFlashSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The beginning of a whole flash image of an ESP32: blank up to the bootloader (0x1000) and the partition table (0x8000).</summary>
    internal static byte[] FullImageHead(byte flashSizeAndFrequency)
    {
        var head = new byte[Esp32ImageInfo.HeadLength];
        Array.Fill(head, (byte)0xFF);
        head[Esp32ImageInfo.BootloaderOffset] = 0xE9;
        head[Esp32ImageInfo.BootloaderOffset + 1] = 3;
        head[Esp32ImageInfo.BootloaderOffset + 2] = 3;
        head[Esp32ImageInfo.BootloaderOffset + 3] = flashSizeAndFrequency;
        head[Esp32ImageInfo.PartitionTableOffset] = 0xAA;
        head[Esp32ImageInfo.PartitionTableOffset + 1] = 0x50;
        return head;
    }
}
