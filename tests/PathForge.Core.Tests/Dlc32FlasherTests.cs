using Dlc32Flasher;

namespace PathForge.Core.Tests;

/// <summary>The built-in flasher of the MKS DLC32 against an emulated ESP32 ROM bootloader.</summary>
public class Dlc32FlasherTests
{
    private const int FlashSize = 1024 * 1024;

    public Dlc32FlasherTests()
    {
        FlasherText.Russian = false;
    }

    /// <summary>A whole flash image: bootloader header at 0x1000, partition table at 0x8000, some application data.</summary>
    private static byte[] FullImage(int length, byte flashSizeCode = 0x10)
    {
        var image = new byte[length];
        new Random(42).NextBytes(image);
        image[0x1000] = 0xE9;
        image[0x1003] = flashSizeCode; // 2 MB in the high nibble (0x10), frequency in the low one
        image[0x8000] = 0xAA;
        image[0x8001] = 0x50;
        // Bytes that need SLIP escaping must survive the trip.
        image[0x8100] = 0xC0;
        image[0x8101] = 0xDB;
        return image;
    }

    private static Dlc32FirmwareFlasher Flasher(FakeEsp32Rom rom, List<string>? log = null) =>
        new(line => log?.Add(line), _ => rom) { Sleep = _ => { } };

    [Fact]
    public async Task Write_PutsTheImageIntoTheFlash_ChecksItAndRestartsTheBoard()
    {
        var rom = new FakeEsp32Rom(FlashSize);
        var image = FullImage(0x9003); // not a multiple of four: padded with 0xFF
        var stages = new List<FlashStage>();

        var report = await Flasher(rom).WriteAsync("COM3", image, new Dlc32FlasherOptions { Baud = 460800 },
            new SyncProgress(p => stages.Add(p.Stage)));

        Assert.True(report.Success, report.Message);
        Assert.Equal(image, rom.Flash[..image.Length]);
        Assert.All(rom.Flash[image.Length..], b => Assert.Equal(0xFF, b));
        Assert.True(rom.Attached);
        Assert.Equal(FlashSize, rom.SizeSet);
        Assert.Equal(460800, rom.BaudRate);
        Assert.Equal(1, rom.ResetsIntoBootloader);
        Assert.Equal(1, rom.HardResets);
        Assert.True(rom.Disposed);
        Assert.Equal(
            new[] { FlashStage.Connecting, FlashStage.Erasing, FlashStage.Writing, FlashStage.Verifying, FlashStage.Restarting },
            stages.Distinct());
    }

    [Fact]
    public async Task Write_CorrectsTheSpeedByTheRomClockCalibration()
    {
        var rom = new FakeEsp32Rom(FlashSize);
        await Flasher(rom).WriteAsync("COM3", FullImage(0x9000), new Dlc32FlasherOptions { Baud = 921600 });
        // The fake calibration says 40 MHz exactly: the speed goes as it is.
        Assert.Equal(921600u, rom.RomBaudAsked);

        var drifted = new FakeEsp32Rom(FlashSize);
        drifted.SetRegister(EspRomLoader.RtcCaliConfig, 808u << 7);
        await Flasher(drifted).WriteAsync("COM3", FullImage(0x9000), new Dlc32FlasherOptions { Baud = 460800 });
        // A clock 1 % fast (40.4 MHz by the ROM's own measure) gets a speed 1 % higher, as esptool sends it.
        Assert.Equal((uint)Math.Floor(460800 * 40.4e6 / 40e6), drifted.RomBaudAsked);
    }

    [Fact]
    public async Task Write_RetriesABlockThatFailedItsChecksum()
    {
        var rom = new FakeEsp32Rom(FlashSize) { ChecksumErrorAtBlock = 5 };
        var log = new List<string>();
        var image = FullImage(0xA000);

        var report = await Flasher(rom, log).WriteAsync("COM3", image);

        Assert.True(report.Success, report.Message);
        Assert.Equal(image, rom.Flash[..image.Length]);
        Assert.Contains(log, l => l.Contains("Block 5"));
    }

    [Fact]
    public async Task Write_ReportsAFlashThatDiffersFromTheFile()
    {
        var rom = new FakeEsp32Rom(FlashSize) { CorruptAfterWrite = true };

        var report = await Flasher(rom).WriteAsync("COM3", FullImage(0x9000));

        Assert.Equal(FlasherResult.Failed, report.Result);
        Assert.Equal(FlasherError.VerifyFailed, report.Error);
        Assert.True(report.FlashChanged);
        Assert.Equal(0, rom.HardResets); // left in the bootloader, ready for another try
    }

    [Fact]
    public async Task Write_RefusesPartsOfAFirmwareWithoutOpeningThePort()
    {
        var opened = false;
        var flasher = new Dlc32FirmwareFlasher(null, _ =>
        {
            opened = true;
            return new FakeEsp32Rom(FlashSize);
        });
        var application = new byte[0x20000];
        application[0] = 0xE9;

        var single = await flasher.WriteAsync("COM3", application);
        var garbage = await flasher.WriteAsync("COM3", new byte[0x20000]);

        Assert.Equal(FlasherError.BadImage, single.Error);
        Assert.Equal(FlasherError.BadImage, garbage.Error);
        Assert.False(opened);
    }

    [Fact]
    public async Task Write_RefusesAnImageLargerThanTheFlash()
    {
        var rom = new FakeEsp32Rom(256 * 1024);

        var report = await Flasher(rom).WriteAsync("COM3", FullImage(0x50000));

        Assert.Equal(FlasherError.DoesNotFit, report.Error);
        Assert.False(report.FlashChanged);
        Assert.Equal(1, rom.HardResets); // nothing was touched: back to the firmware
        Assert.All(rom.Flash, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public async Task Write_CancelledInTheMiddle_SaysTheFlashWasChanged()
    {
        using var cts = new CancellationTokenSource();
        var rom = new FakeEsp32Rom(FlashSize) { OnBlock = sequence => { if (sequence == 3) cts.Cancel(); } };

        var report = await Flasher(rom).WriteAsync("COM3", FullImage(0x10000), token: cts.Token);

        Assert.Equal(FlasherResult.Cancelled, report.Result);
        Assert.True(report.FlashChanged);
    }

    [Fact]
    public async Task Connect_WithoutReset_TalksToABoardPutIntoTheBootloaderByHand()
    {
        var rom = new FakeEsp32Rom(FlashSize) { LinesNotWired = true, InBootloader = true };

        var report = await Flasher(rom).WriteAsync("COM3", FullImage(0x9000),
            new Dlc32FlasherOptions { ResetIntoBootloader = false, RestartAfter = false });

        Assert.True(report.Success, report.Message);
        Assert.Equal(0, rom.ResetsIntoBootloader);
    }

    [Fact]
    public async Task Connect_TellsAFirmwareBootFromNoAnswerAtAll()
    {
        var firmware = new FakeEsp32Rom(FlashSize) { Gpio0NotWired = true };
        var silent = new FakeEsp32Rom(FlashSize) { LinesNotWired = true };
        var options = new Dlc32FlasherOptions { ConnectAttempts = 1 };

        var wrongMode = await Flasher(firmware).WriteAsync("COM3", FullImage(0x9000), options);
        var noAnswer = await Flasher(silent).WriteAsync("COM3", FullImage(0x9000), options);

        Assert.Equal(FlasherError.WrongBootMode, wrongMode.Error);
        Assert.Equal(FlasherError.NoAnswer, noAnswer.Error);
        Assert.False(wrongMode.FlashChanged);
    }

    [Fact]
    public async Task Connect_RefusesAnotherChip()
    {
        var rom = new FakeEsp32Rom(FlashSize) { Magic = 0x000007C6 }; // ESP32-S2

        var report = await Flasher(rom).WriteAsync("COM3", FullImage(0x9000));

        Assert.Equal(FlasherError.WrongChip, report.Error);
        Assert.Contains("0x000007C6", report.Message);
    }

    [Fact]
    public async Task Read_CopiesTheWholeFlashIntoAFile()
    {
        var rom = new FakeEsp32Rom(256 * 1024);
        var image = FullImage(0x9000);
        image.CopyTo(rom.Flash, 0);
        var path = Path.Combine(Path.GetTempPath(), $"dlc32-{Guid.NewGuid():N}.bin");
        try
        {
            var report = await Flasher(rom).ReadFileAsync("COM3", path);

            Assert.True(report.Success, report.Message);
            Assert.Equal(rom.Flash, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(path + ".part"));
            Assert.Contains("partition table are in the copy", report.Message);
            Assert.Equal(1, rom.HardResets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Read_FailedConnection_LeavesAnExistingFileAlone()
    {
        var rom = new FakeEsp32Rom(256 * 1024) { LinesNotWired = true };
        var path = Path.Combine(Path.GetTempPath(), $"dlc32-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        try
        {
            var report = await Flasher(rom).ReadFileAsync("COM3", path, new Dlc32FlasherOptions { ConnectAttempts = 1 });

            Assert.Equal(FlasherError.NoAnswer, report.Error);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0x164020u, 4L << 20)]
    [InlineData(0x174020u, 8L << 20)]
    [InlineData(0x1840EFu, 16L << 20)]
    [InlineData(0x3725C2u, 8L << 20)]
    [InlineData(0x0000FFu, 0L)]
    public void FlashSize_ComesFromTheCapacityCodeOfTheJedecId(uint id, long size) =>
        Assert.Equal(size, EspRomLoader.FlashSizeFromId(id));

    [Fact]
    public void Image_IsJudgedByItsBootloaderAndPartitionTable()
    {
        var full = FirmwareImageInfo.Inspect(FullImage(0x9000, flashSizeCode: 0x30));
        Assert.Equal(FirmwareImageKind.FullFlash, full.Kind);
        Assert.Equal(8L << 20, full.DeclaredFlashSize);
        Assert.Null(full.RefusalReason());

        var app = new byte[0x100];
        app[0] = 0xE9;
        Assert.Equal(FirmwareImageKind.SingleImage, FirmwareImageInfo.Inspect(app).Kind);
        Assert.NotNull(FirmwareImageInfo.Inspect(app).RefusalReason());
        Assert.Equal(FirmwareImageKind.Unrecognized, FirmwareImageInfo.Inspect(new byte[0x9000]).Kind);
    }

    [Fact]
    public void Checksum_StartsFrom0xEF()
    {
        Assert.Equal(0xEFu, EspRomLoader.Checksum(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0xEFu ^ 0x01 ^ 0xFF, EspRomLoader.Checksum(new byte[] { 0x01, 0xFF }));
    }

    [Fact]
    public void Slip_EscapesTheDelimiterAndTheEscape()
    {
        Assert.Equal(new byte[] { 0xC0, 0x01, 0xDB, 0xDC, 0xDB, 0xDD, 0x02, 0xC0 }, Slip.Encode(new byte[] { 0x01, 0xC0, 0xDB, 0x02 }));
    }

    [Fact]
    public void Texts_FollowTheChosenLanguage()
    {
        try
        {
            FlasherText.Russian = true;
            Assert.Equal("Запись: 50 %", new FlashProgress(FlashStage.Writing, 50, 100).Text);
            FlasherText.Russian = false;
            Assert.Equal("Writing: 50 %", new FlashProgress(FlashStage.Writing, 50, 100).Text);
        }
        finally
        {
            FlasherText.Russian = false;
        }
    }

    /// <summary>IProgress that reports at once on the calling thread (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress : IProgress<FlashProgress>
    {
        private readonly Action<FlashProgress> _report;

        public SyncProgress(Action<FlashProgress> report)
        {
            _report = report;
        }

        public void Report(FlashProgress value)
        {
            lock (this)
            {
                _report(value);
            }
        }
    }
}
