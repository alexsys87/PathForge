namespace PathForge.Core.Grbl;

/// <summary>What a firmware file of an ESP32 board is.</summary>
public enum Esp32ImageKind
{
    /// <summary>
    /// A whole flash image that starts at address 0: the bootloader at 0x1000 and the partition table at 0x8000 are in place.
    /// The firmware files of the MKS DLC32 and the dumps of its flash are such images.
    /// </summary>
    FullFlash,

    /// <summary>
    /// One image (a bootloader or an application) that starts with its own header. It belongs at an offset of its own
    /// (0x1000, 0x10000), and written at 0 it would ruin the flash.
    /// </summary>
    SingleImage,

    /// <summary>Neither of the two: not a firmware, a damaged file, or a flash that was never written.</summary>
    Unrecognized,
}

/// <summary>What the first bytes of a firmware file tell about it, before it is written to the board.</summary>
/// <param name="Kind">What kind of image it is.</param>
/// <param name="Length">Size of the file in bytes.</param>
/// <param name="DeclaredFlashSize">Flash size the bootloader header asks for ("4MB", "8MB"), if it can be told.</param>
public sealed record Esp32ImageInfo(Esp32ImageKind Kind, long Length, string? DeclaredFlashSize)
{
    /// <summary>Where the second-stage bootloader sits in the flash of an ESP32.</summary>
    public const int BootloaderOffset = 0x1000;

    /// <summary>Where the partition table sits in the flash of an ESP32.</summary>
    public const int PartitionTableOffset = 0x8000;

    /// <summary>How much of the file has to be read to judge it.</summary>
    public const int HeadLength = PartitionTableOffset + 2;

    private const byte ImageMagic = 0xE9;

    public static Esp32ImageInfo Inspect(ReadOnlySpan<byte> head, long length)
    {
        if (head.Length >= HeadLength
            && head[BootloaderOffset] == ImageMagic
            && head[PartitionTableOffset] == 0xAA
            && head[PartitionTableOffset + 1] == 0x50)
        {
            // Byte 3 of the image header: the flash size in the high nibble, the frequency in the low one.
            var declared = (head[BootloaderOffset + 3] >> 4) switch
            {
                0 => "1MB",
                1 => "2MB",
                2 => "4MB",
                3 => "8MB",
                4 => "16MB",
                _ => null,
            };
            return new Esp32ImageInfo(Esp32ImageKind.FullFlash, length, declared);
        }

        if (head.Length > 0 && head[0] == ImageMagic)
        {
            return new Esp32ImageInfo(Esp32ImageKind.SingleImage, length, null);
        }

        return new Esp32ImageInfo(Esp32ImageKind.Unrecognized, length, null);
    }

    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    public static Esp32ImageInfo InspectFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[HeadLength];
        var count = 0;
        while (count < head.Length)
        {
            var read = stream.Read(head, count, head.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return Inspect(head.AsSpan(0, count), stream.Length);
    }
}
