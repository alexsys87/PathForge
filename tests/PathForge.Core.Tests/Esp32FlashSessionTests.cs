using PathForge.Core.Grbl;

namespace PathForge.Core.Tests;

/// <summary>
/// esptool as a program of its own, played by the test: it prints what the real one prints (the formats of version 5 and of
/// version 4) and does what it does, on a board that the test sets up.
/// </summary>
internal sealed class FakeEsptool : IEsptoolProcess
{
    private const int Megabyte = 1024 * 1024;

    /// <summary>The installed version: "5.5.0" prints "esptool v5.5.0", "4.8.1" prints "esptool.py v4.8.1".</summary>
    public string Version { get; set; } = "5.5.0";

    /// <summary>What flash-id prints after "Detected flash size:".</summary>
    public string FlashSize { get; set; } = "8MB";

    /// <summary>A board without the auto-reset circuit: it answers only when esptool leaves the reset alone.</summary>
    public bool NeedsNoReset { get; set; }

    /// <summary>The dump of the flash holds an image (the bootloader and the partition table) rather than blank flash.</summary>
    public bool DumpHasImage { get; set; } = true;

    /// <summary>Starts a probe of a way to start the tool; <c>null</c> lets it answer as an installed esptool does.</summary>
    public Func<EsptoolTool, Action<string>, CancellationToken, Task<int>?>? OnProbe { get; set; }

    public Func<string[], Action<string>, CancellationToken, int>? OnFlashId { get; set; }

    public Func<string[], Action<string>, CancellationToken, int>? OnRead { get; set; }

    public Func<string[], Action<string>, CancellationToken, int>? OnWrite { get; set; }

    /// <summary>The arguments of every run, in order.</summary>
    public List<string[]> Calls { get; } = new();

    /// <summary>The tool of every run, in order.</summary>
    public List<EsptoolTool> Tools { get; } = new();

    /// <summary>The command of every run: "version", "flash-id", "read-flash", "write-flash".</summary>
    public IEnumerable<string> Commands => Calls.Select(CommandOf);

    public static string CommandOf(string[] args)
    {
        if (args.Length == 1 && args[0] == "version")
        {
            return "version";
        }

        // The options with a value come first: --chip, --port, --baud, --before, --after.
        var i = 0;
        while (i < args.Length && args[i] is "--chip" or "--port" or "--baud" or "--before" or "--after")
        {
            i += 2;
        }

        return i < args.Length ? args[i].Replace('_', '-') : "";
    }

    public static string ValueOf(string[] args, string option)
    {
        var i = Array.IndexOf(args, option);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
    }

    public Task<int> RunAsync(EsptoolTool tool, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var args = arguments.ToArray();
        Calls.Add(args);
        Tools.Add(tool);

        if (CommandOf(args) == "version")
        {
            return OnProbe?.Invoke(tool, onLine, token) ?? Task.FromResult(Probe(onLine));
        }

        // A board without auto-reset does not hear esptool unless esptool leaves the reset alone.
        var before = ValueOf(args, "--before");
        if (NeedsNoReset && before is not ("no-reset" or "no_reset"))
        {
            onLine("A fatal error occurred: Failed to connect to ESP32: No serial data received.");
            return Task.FromResult(2);
        }

        var dashes = tool.Version.UsesDashes;
        switch (CommandOf(args))
        {
            case "flash-id":
                return Task.FromResult(OnFlashId?.Invoke(args, onLine, token) ?? FlashId(args, onLine));
            case "read-flash":
                return Task.FromResult(OnRead?.Invoke(args, onLine, token) ?? Read(args, onLine, dashes));
            case "write-flash":
                return Task.FromResult(OnWrite?.Invoke(args, onLine, token) ?? Write(args, onLine, dashes));
            default:
                onLine("A fatal error occurred: unknown command");
                return Task.FromResult(2);
        }
    }

    /// <summary>The progress lines of the version of the tool: a bar in version 5, a count in version 4.</summary>
    public static string ProgressLine(bool dashes, string verb, int offset, double percent) =>
        dashes
            ? FormattableString.Invariant($"{verb} 0x{offset:x8} ━━━━━━━━━━━━━━                    {percent:0.0}% 390.62kB/1.91MB [0s] ")
            : FormattableString.Invariant($"{verb} at 0x{offset:x8}... ({percent:0} %)");

    /// <summary>Makes the file the real esptool would leave after reading <paramref name="size"/> bytes of flash.</summary>
    public void WriteDump(string path, int size)
    {
        using var stream = File.Create(path);
        if (DumpHasImage)
        {
            stream.Write(EsptoolTests.FullImageHead(0x3F));
        }

        stream.SetLength(size);
    }

    private int Probe(Action<string> onLine)
    {
        var banner = Version.StartsWith("4.", StringComparison.Ordinal) ? "esptool.py" : "esptool";
        onLine($"{banner} v{Version}");
        onLine(Version);
        return 0;
    }

    private int FlashId(string[] args, Action<string> onLine)
    {
        onLine("esptool v" + Version);
        onLine("Serial port COM5:");
        onLine("Connecting....");
        onLine("Chip type:          ESP32-D0WD-V3 (revision v3.1)");
        onLine("Manufacturer: ef");
        onLine("Device: 4017");
        onLine($"Detected flash size: {FlashSize}");
        if (ValueOf(args, "--after") is "no-reset" or "no_reset")
        {
            onLine("Staying in bootloader.");
        }

        return 0;
    }

    private int Read(string[] args, Action<string> onLine, bool dashes)
    {
        var at = Array.FindIndex(args, a => a is "read-flash" or "read_flash");
        var size = Convert.ToInt32(args[at + 2], 16);
        var file = args[at + 3];

        for (var percent = 0; percent <= 100; percent += 5)
        {
            onLine(ProgressLine(dashes, "Reading from", size / 100 * percent, percent));
        }

        WriteDump(file, size);
        onLine("Hard resetting via RTS pin...");
        return 0;
    }

    private int Write(string[] args, Action<string> onLine, bool dashes)
    {
        for (var percent = 0; percent <= 100; percent += 5)
        {
            onLine(ProgressLine(dashes, "Writing at", Megabyte / 100 * percent, percent));
        }

        onLine("Wrote 8327168 bytes (1204331 compressed) at 0x00000000 in 31.2 seconds (effective 2135.0 kbit/s)...");
        onLine("Hash of data verified.");
        onLine("Hard resetting via RTS pin...");
        return 0;
    }
}

/// <summary>Reading and writing the flash of the board through esptool.</summary>
public sealed class Esp32FlashSessionTests
{
    private const int Megabyte = 1024 * 1024;

    // ---- Finding the tool -----------------------------------------------------------------------

    [Fact]
    public async Task Tool_is_the_first_way_to_start_esptool_that_answers_version()
    {
        using var rig = new Rig(tool =>
        {
            // "esptool" is not on the search path, "python" has no such module, "py" has it.
            tool.OnProbe = (candidate, onLine, _) =>
            {
                switch (candidate.FileName)
                {
                    case "esptool":
                        throw new EsptoolNotStartedException("esptool");
                    case "python":
                        onLine("C:\\Python\\python.exe: No module named esptool");
                        return Task.FromResult(1);
                    default:
                        return null;
                }
            };
        });

        var found = await rig.Session.FindToolAsync(CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("py -3 -m esptool", found!.Describe());
        Assert.Equal(new EsptoolVersion(5, 5), found.Version);
        Assert.Equal(new[] { "esptool", "python", "py" }, rig.Tool.Tools.Select(t => t.FileName).ToArray());
    }

    [Fact]
    public async Task Tool_is_found_once_and_kept()
    {
        using var rig = new Rig();

        await rig.Session.FindToolAsync(CancellationToken.None);
        await rig.Session.FindToolAsync(CancellationToken.None);

        Assert.Single(rig.Tool.Calls);
    }

    [Fact]
    public async Task Named_file_is_tried_first_and_naming_another_one_starts_the_search_again()
    {
        var first = Path.Combine("D:", "esp", "esptool.exe");
        var second = Path.Combine("E:", "esp", "esptool.exe");
        using var rig = new Rig(fileExists: path => path == first || path == second);

        rig.Session.ExplicitToolPath = first;
        var one = await rig.Session.FindToolAsync(CancellationToken.None);
        rig.Session.ExplicitToolPath = second;
        var two = await rig.Session.FindToolAsync(CancellationToken.None);

        Assert.Equal(first, one!.FileName);
        Assert.Equal(second, two!.FileName);
    }

    [Fact]
    public async Task Tool_that_never_answers_is_given_up_after_the_timeout()
    {
        using var rig = new Rig(
            tool => tool.OnProbe = (candidate, _, token) =>
                candidate.FileName == "esptool" ? Task.Delay(Timeout.Infinite, token).ContinueWith(_ => 0, token) : null,
            probeTimeout: TimeSpan.FromMilliseconds(50));

        var found = await rig.Session.FindToolAsync(CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("python -m esptool", found!.Describe());
    }

    [Fact]
    public async Task Searching_for_the_tool_can_be_cancelled()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = false;
        try
        {
            await rig.Session.FindToolAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.True(cancelled);
    }

    [Fact]
    public async Task Missing_tool_is_reported_with_what_to_do()
    {
        using var rig = new Rig(tool => tool.OnProbe = (_, _, _) => throw new EsptoolNotStartedException("x"));

        var read = await rig.Session.ReadAsync("COM5", 460800, rig.File("dump.bin"), CancellationToken.None);
        var write = await rig.Session.WriteAsync("COM5", 460800, rig.File("fw.bin"), CancellationToken.None);

        Assert.Equal(FlashResult.ToolNotFound, read.Result);
        Assert.Equal(FlashResult.ToolNotFound, write.Result);
        Assert.False(read.Success);
        Assert.Contains("esptool", read.Message);
        Assert.Contains("pip install esptool", read.Message);
    }

    [Fact]
    public async Task Tool_that_disappears_after_it_was_found_is_reported_as_missing_and_searched_for_again_next_time()
    {
        using var rig = new Rig(tool => tool.OnFlashId = (_, _, _) => throw new EsptoolNotStartedException("esptool"));
        var output = rig.File("gone.bin");

        var outcome = await rig.Session.ReadAsync("COM5", 460800, output, CancellationToken.None);

        Assert.Equal(FlashResult.ToolNotFound, outcome.Result);
        Assert.False(outcome.WriteStarted);

        // The cached tool is dropped: the next operation probes again.
        Assert.Null(rig.Session.Tool);

        // The same for a write.
        var image = rig.File("fw.bin");
        File.WriteAllBytes(image, new byte[1000]);
        await rig.Session.FindToolAsync(CancellationToken.None);
        var write = await rig.Session.WriteAsync("COM5", 460800, image, CancellationToken.None);
        Assert.Equal(FlashResult.ToolNotFound, write.Result);
        Assert.False(write.WriteStarted);
        Assert.Null(rig.Session.Tool);
    }

    // ---- Reading ---------------------------------------------------------------------------------

    [Fact]
    public async Task Reading_asks_the_chip_for_the_size_of_its_flash_and_reads_all_of_it()
    {
        using var rig = new Rig();
        var output = rig.File("dlc32.bin");

        var outcome = await rig.Session.ReadAsync("COM5", 460800, output, CancellationToken.None);

        Assert.Equal(FlashResult.Done, outcome.Result);
        Assert.True(outcome.Success);
        Assert.Equal(new[] { "version", "flash-id", "read-flash" }, rig.Tool.Commands.ToArray());

        // The command line of esptool 5, with the size that the chip reported (8 MB) and the file the operator chose.
        var read = rig.Tool.Calls[2];
        Assert.Equal(
            new[] { "--chip", "esp32", "--port", "COM5", "--baud", "460800", "read-flash", "0x0", "0x800000", output + ".part" },
            read);
        Assert.False(File.Exists(output + ".part"));
        Assert.Equal(8 * Megabyte, new FileInfo(output).Length);
        Assert.Contains("dlc32.bin", outcome.Message);
        Assert.Contains("8", outcome.Message);
    }

    [Fact]
    public async Task Reading_with_esptool_4_uses_its_spelling()
    {
        using var rig = new Rig(tool => tool.Version = "4.8.1");

        var outcome = await rig.Session.ReadAsync("COM5", 115200, rig.File("a.bin"), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(new[] { "version", "flash-id", "read-flash" }, rig.Tool.Commands.ToArray());
        Assert.Equal("read_flash", rig.Tool.Calls[2][6]);
        Assert.Equal("flash_id", rig.Tool.Calls[1][8]);
        Assert.Equal("no_reset", FakeEsptool.ValueOf(rig.Tool.Calls[1], "--after"));
    }

    [Fact]
    public async Task Reading_takes_the_size_the_chip_reports_even_when_it_is_not_the_usual_one()
    {
        using var rig = new Rig(tool => tool.FlashSize = "4MB");
        var output = rig.File("old-board.bin");

        var outcome = await rig.Session.ReadAsync("COM5", 460800, output, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("0x400000", rig.Tool.Calls[2][8]);
        Assert.Equal(4 * Megabyte, new FileInfo(output).Length);

        // The operator is told that this is not the 8 MB of the MKS DLC32.
        Assert.Contains(
            rig.Log,
            line => !line.StartsWith("esptool:", StringComparison.Ordinal) && line.Contains('4') && line.Contains('8'));
    }

    [Fact]
    public async Task Reading_takes_8_megabytes_when_the_chip_does_not_tell_the_size()
    {
        using var rig = new Rig(tool => tool.FlashSize = "Unknown");

        var outcome = await rig.Session.ReadAsync("COM5", 460800, rig.File("x.bin"), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("0x800000", rig.Tool.Calls[2][8]);
    }

    [Fact]
    public async Task Reading_warns_when_the_dump_holds_no_image()
    {
        using var rig = new Rig(tool => tool.DumpHasImage = false);

        var outcome = await rig.Session.ReadAsync("COM5", 460800, rig.File("blank.bin"), CancellationToken.None);

        // The file is complete, so it is kept, but the operator is warned in either language.
        Assert.True(outcome.Success);
        var warned = new[] { "Внимание", "Warning" }.Any(word => outcome.Message.Contains(word, StringComparison.Ordinal));
        Assert.True(warned);
        Assert.True(File.Exists(rig.File("blank.bin")));
    }

    [Fact]
    public async Task Reading_moves_the_bar_from_the_size_request_to_the_end_without_stepping_back()
    {
        using var rig = new Rig();

        await rig.Session.ReadAsync("COM5", 460800, rig.File("p.bin"), CancellationToken.None);

        var percents = rig.Progress.Select(p => p.Percent).ToList();
        Assert.Equal(0.0, percents[0]);
        Assert.Equal(100.0, percents[^1]);
        for (var i = 1; i < percents.Count; i++)
        {
            Assert.True(percents[i] >= percents[i - 1]);
        }

        // The size request takes the first few percent, the reading the rest.
        Assert.Contains(percents, p => p is > 5 and < 100);
        Assert.True(rig.Progress.Select(p => p.Text).Distinct().Count() > 3);
    }

    [Fact]
    public async Task Reading_shows_what_esptool_says_but_not_every_progress_line()
    {
        using var rig = new Rig();

        await rig.Session.ReadAsync("COM5", 460800, rig.File("log.bin"), CancellationToken.None);

        Assert.Contains("esptool: Detected flash size: 8MB", rig.Log);
        Assert.Contains("esptool: Hard resetting via RTS pin...", rig.Log);
        Assert.DoesNotContain(rig.Log, line => line.Contains('%'));
        Assert.DoesNotContain(rig.Log, line => line.Contains('━'));
    }

    [Fact]
    public async Task Progress_is_reported_in_half_percent_steps_however_many_lines_esptool_prints()
    {
        using var rig = new Rig(tool => tool.OnRead = (args, onLine, _) =>
        {
            // A line for each block: a thousand of them for the whole flash.
            for (var i = 0; i <= 1000; i++)
            {
                onLine(FakeEsptool.ProgressLine(true, "Reading from", i * 8000, i / 10.0));
            }

            tool.WriteDump(args[^1], 8 * Megabyte);
            return 0;
        });

        await rig.Session.ReadAsync("COM5", 460800, rig.File("many.bin"), CancellationToken.None);

        Assert.True(rig.Progress.Count < 260);
        Assert.True(rig.Progress.Count > 150);
    }

    [Fact]
    public async Task Board_without_auto_reset_is_asked_again_without_a_reset_and_stays_that_way()
    {
        using var rig = new Rig(tool => tool.NeedsNoReset = true);

        var outcome = await rig.Session.ReadAsync("COM5", 460800, rig.File("manual.bin"), CancellationToken.None);

        Assert.True(outcome.Success);

        // flash-id: refused with the reset, answered without it; the reading does not try the reset again.
        Assert.Equal(new[] { "version", "flash-id", "flash-id", "read-flash" }, rig.Tool.Commands.ToArray());
        Assert.Equal("", FakeEsptool.ValueOf(rig.Tool.Calls[1], "--before"));
        Assert.Equal("no-reset", FakeEsptool.ValueOf(rig.Tool.Calls[2], "--before"));
        Assert.Equal("no-reset", FakeEsptool.ValueOf(rig.Tool.Calls[3], "--before"));
        Assert.Contains(rig.Log, line => line.Contains("DTR/RTS"));
    }

    [Fact]
    public async Task Board_that_does_not_answer_at_all_is_reported_after_the_second_try()
    {
        using var rig = new Rig(tool => tool.OnFlashId = (_, onLine, _) =>
        {
            onLine("A fatal error occurred: Failed to connect to ESP32: No serial data received.");
            return 2;
        });

        var outcome = await rig.Session.ReadAsync("COM5", 460800, rig.File("none.bin"), CancellationToken.None);

        Assert.Equal(FlashResult.Failed, outcome.Result);
        Assert.Equal(EsptoolFailure.NoConnection, outcome.Failure);
        Assert.Equal(new[] { "version", "flash-id", "flash-id" }, rig.Tool.Commands.ToArray());
        Assert.Contains("12", outcome.Message);
        Assert.Contains("No serial data received", outcome.Message);
    }

    [Fact]
    public async Task Transfer_that_has_begun_is_not_repeated_behind_the_operators_back()
    {
        // Whatever the message says: once data has flowed, a failure is reported, not retried.
        using var rig = new Rig(tool => tool.OnRead = (_, onLine, _) =>
        {
            onLine(FakeEsptool.ProgressLine(true, "Reading from", 0x100000, 12.5));
            onLine("A fatal error occurred: Failed to connect to ESP32: No serial data received.");
            return 2;
        });

        var outcome = await rig.Session.ReadAsync("COM5", 460800, rig.File("begun.bin"), CancellationToken.None);

        Assert.Equal(FlashResult.Failed, outcome.Result);
        Assert.Equal(new[] { "version", "flash-id", "read-flash" }, rig.Tool.Commands.ToArray());
    }

    [Fact]
    public async Task Port_that_is_busy_is_reported_at_once_without_a_second_try()
    {
        using var rig = new Rig(tool => tool.OnFlashId = (_, onLine, _) =>
        {
            onLine("A fatal error occurred: Could not open COM5, the port is busy or doesn't exist.");
            onLine("(could not open port 'COM5': PermissionError(13, 'Access is denied.', None, 5))");
            return 2;
        });

        var outcome = await rig.Session.ReadAsync("COM5", 460800, rig.File("busy.bin"), CancellationToken.None);

        Assert.Equal(EsptoolFailure.PortUnavailable, outcome.Failure);
        Assert.Equal(new[] { "version", "flash-id" }, rig.Tool.Commands.ToArray());
    }

    [Fact]
    public async Task Read_that_breaks_off_removes_the_partial_file_and_says_why()
    {
        using var rig = new Rig(tool => tool.OnRead = (args, onLine, _) =>
        {
            onLine(FakeEsptool.ProgressLine(true, "Reading from", 0x100000, 12.5));
            File.WriteAllBytes(args[^1], new byte[1000]);
            onLine("A fatal error occurred: The chip stopped responding.");
            return 2;
        });
        var output = rig.File("partial.bin");

        var outcome = await rig.Session.ReadAsync("COM5", 921600, output, CancellationToken.None);

        Assert.Equal(EsptoolFailure.ChipStopped, outcome.Failure);
        Assert.False(File.Exists(output));
        Assert.False(outcome.WriteStarted);
    }

    [Fact]
    public async Task File_that_is_shorter_than_the_flash_is_not_taken_for_a_dump()
    {
        using var rig = new Rig(tool => tool.OnRead = (args, _, _) =>
        {
            File.WriteAllBytes(args[^1], new byte[Megabyte]);
            return 0;
        });
        var output = rig.File("short.bin");

        var outcome = await rig.Session.ReadAsync("COM5", 460800, output, CancellationToken.None);

        Assert.Equal(FlashResult.Failed, outcome.Result);
        Assert.False(File.Exists(output));
        Assert.Contains(Megabyte.ToString(), outcome.Message);
    }

    [Fact]
    public async Task Reading_can_be_cancelled_and_leaves_no_partial_file()
    {
        using var cts = new CancellationTokenSource();
        using var rig = new Rig(tool => tool.OnRead = (args, _, token) =>
        {
            File.WriteAllBytes(args[^1], new byte[10]);
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return 0;
        });
        var output = rig.File("cancelled.bin");

        var outcome = await rig.Session.ReadAsync("COM5", 460800, output, cts.Token);

        Assert.Equal(FlashResult.Cancelled, outcome.Result);
        Assert.False(File.Exists(output));
    }

    // ---- Writing ---------------------------------------------------------------------------------

    [Fact]
    public async Task Writing_takes_the_image_from_address_zero_and_lets_esptool_check_it()
    {
        using var rig = new Rig();
        var image = rig.File("firmware.bin");
        File.WriteAllBytes(image, new byte[8_327_168]);

        var outcome = await rig.Session.WriteAsync("COM5", 460800, image, CancellationToken.None);

        Assert.Equal(FlashResult.Done, outcome.Result);
        Assert.Equal(new[] { "version", "flash-id", "write-flash" }, rig.Tool.Commands.ToArray());
        Assert.Equal(
            new[] { "--chip", "esp32", "--port", "COM5", "--baud", "460800", "write-flash", "--flash-size", "detect", "0x0", image },
            rig.Tool.Calls[2]);
        Assert.False(outcome.WriteStarted);

        // The operator is reminded that the settings of GRBL live in the flash.
        Assert.Contains("$$", outcome.Message);
    }

    [Fact]
    public async Task Writing_with_esptool_4_uses_its_spelling()
    {
        using var rig = new Rig(tool => tool.Version = "4.8.1");
        var image = rig.File("firmware.bin");
        File.WriteAllBytes(image, new byte[1000]);

        var outcome = await rig.Session.WriteAsync("COM5", 460800, image, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("write_flash", rig.Tool.Calls[2][6]);
        Assert.Equal("--flash_size", rig.Tool.Calls[2][7]);
    }

    [Fact]
    public async Task Image_larger_than_the_flash_of_the_board_is_refused_before_anything_is_written()
    {
        // An 8 MB firmware for a board that reports 4 MB of flash.
        using var rig = new Rig(tool => tool.FlashSize = "4MB");
        var image = rig.File("big.bin");
        File.WriteAllBytes(image, new byte[8_327_168]);

        var outcome = await rig.Session.WriteAsync("COM5", 460800, image, CancellationToken.None);

        Assert.Equal(FlashResult.Failed, outcome.Result);
        Assert.Equal(EsptoolFailure.DoesNotFit, outcome.Failure);
        Assert.False(outcome.WriteStarted);
        Assert.Equal(new[] { "version", "flash-id" }, rig.Tool.Commands.ToArray());
    }

    [Fact]
    public async Task Failed_check_after_writing_is_reported_and_marks_the_flash_as_touched()
    {
        using var rig = new Rig(tool => tool.OnWrite = (_, onLine, _) =>
        {
            onLine(FakeEsptool.ProgressLine(true, "Writing at", 0x100000, 100));
            onLine("A fatal error occurred: MD5 of file does not match data in flash!");
            return 2;
        });
        var image = rig.File("fw.bin");
        File.WriteAllBytes(image, new byte[1000]);

        var outcome = await rig.Session.WriteAsync("COM5", 460800, image, CancellationToken.None);

        Assert.Equal(FlashResult.Failed, outcome.Result);
        Assert.Equal(EsptoolFailure.VerifyFailed, outcome.Failure);
        Assert.True(outcome.WriteStarted);
    }

    [Fact]
    public async Task Board_that_cannot_be_reached_has_not_been_touched_by_a_write()
    {
        using var rig = new Rig(tool => tool.OnWrite = (_, onLine, _) =>
        {
            onLine("A fatal error occurred: Failed to connect to ESP32: No serial data received.");
            return 2;
        });
        var image = rig.File("fw.bin");
        File.WriteAllBytes(image, new byte[1000]);

        var outcome = await rig.Session.WriteAsync("COM5", 460800, image, CancellationToken.None);

        Assert.Equal(EsptoolFailure.NoConnection, outcome.Failure);
        Assert.False(outcome.WriteStarted);
    }

    [Fact]
    public async Task Writing_cancelled_before_it_starts_leaves_the_board_unchanged()
    {
        using var cts = new CancellationTokenSource();
        using var rig = new Rig(tool => tool.OnFlashId = (_, _, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return 0;
        });
        var image = rig.File("fw.bin");
        File.WriteAllBytes(image, new byte[1000]);

        var outcome = await rig.Session.WriteAsync("COM5", 460800, image, cts.Token);

        Assert.Equal(FlashResult.Cancelled, outcome.Result);
        Assert.False(outcome.WriteStarted);
        Assert.Equal(new[] { "version", "flash-id" }, rig.Tool.Commands.ToArray());
    }

    [Fact]
    public async Task Writing_cancelled_midway_warns_that_the_flash_may_be_broken()
    {
        using var cts = new CancellationTokenSource();
        using var rig = new Rig(tool => tool.OnWrite = (_, onLine, token) =>
        {
            onLine(FakeEsptool.ProgressLine(true, "Writing at", 0x100000, 40));
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return 0;
        });
        var image = rig.File("fw.bin");
        File.WriteAllBytes(image, new byte[1000]);

        var outcome = await rig.Session.WriteAsync("COM5", 460800, image, cts.Token);

        Assert.Equal(FlashResult.Cancelled, outcome.Result);
        Assert.True(outcome.WriteStarted);
    }

    [Fact]
    public async Task Failed_or_cancelled_read_leaves_an_older_file_of_the_same_name_alone()
    {
        using var rig = new Rig(tool => tool.OnFlashId = (_, onLine, _) =>
        {
            onLine("A fatal error occurred: Failed to connect to ESP32: No serial data received.");
            return 2;
        });
        var output = rig.File("backup.bin");
        File.WriteAllBytes(output, new byte[] { 1, 2, 3 });

        var outcome = await rig.Session.ReadAsync("COM5", 460800, output, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task Write_refused_by_esptool_as_too_large_has_not_touched_the_board()
    {
        using var rig = new Rig(tool => tool.OnWrite = (_, onLine, _) =>
        {
            onLine("A fatal error occurred: File fw.bin (length 9000000) at offset 0 will not fit in 8388608 bytes of flash.");
            return 2;
        });
        var image = rig.File("fw.bin");
        File.WriteAllBytes(image, new byte[1000]);

        var outcome = await rig.Session.WriteAsync("COM5", 460800, image, CancellationToken.None);

        Assert.Equal(EsptoolFailure.DoesNotFit, outcome.Failure);
        Assert.False(outcome.WriteStarted);
    }

    /// <summary>A session on the fake esptool, with a folder of its own for the files.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("pathforge-flash-").FullName;

        public Rig(Action<FakeEsptool>? setup = null, Func<string, bool>? fileExists = null, TimeSpan? probeTimeout = null)
        {
            setup?.Invoke(Tool);
            Session = new Esp32FlashSession(
                Tool,
                "C:\\PathForge",
                line =>
                {
                    lock (Log)
                    {
                        Log.Add(line);
                    }
                },
                (percent, text) =>
                {
                    lock (Progress)
                    {
                        Progress.Add((percent, text));
                    }
                },
                fileExists ?? (_ => false),
                windows: true)
            {
                ProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(20),
            };
        }

        public FakeEsptool Tool { get; } = new();

        public Esp32FlashSession Session { get; }

        public List<string> Log { get; } = new();

        public List<(double Percent, string Text)> Progress { get; } = new();

        public string File(string name) => Path.Combine(_directory, name);

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
