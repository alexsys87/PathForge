namespace Dlc32Flasher;

/// <summary>What a firmware file of an ESP32 board is.</summary>
public enum FirmwareImageKind
{
    /// <summary>
    /// A whole flash image that starts at address 0: the bootloader at 0x1000 and the partition table at 0x8000 are in place.
    /// The firmware files of the MKS DLC32 and the dumps of its flash are such images.
    /// </summary>
    FullFlash,

    /// <summary>One image (a bootloader or an application) with its own header: it belongs at an offset of its own, not at 0.</summary>
    SingleImage,

    /// <summary>Neither: not a firmware, a damaged file, or an empty flash.</summary>
    Unrecognized,
}

/// <summary>What the first bytes of a firmware file tell about it.</summary>
/// <param name="Kind">What kind of image it is.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="DeclaredFlashSize">Flash size in bytes that the bootloader header asks for, or 0 when it cannot be told.</param>
public sealed record FirmwareImageInfo(FirmwareImageKind Kind, long Length, long DeclaredFlashSize)
{
    /// <summary>Where the second-stage bootloader sits in the flash of an ESP32.</summary>
    public const int BootloaderOffset = 0x1000;

    /// <summary>Where the partition table sits in the flash of an ESP32.</summary>
    public const int PartitionTableOffset = 0x8000;

    private const byte ImageMagic = 0xE9;

    public static FirmwareImageInfo Inspect(ReadOnlySpan<byte> image)
    {
        if (image.Length >= PartitionTableOffset + 2
            && image[BootloaderOffset] == ImageMagic
            && image[PartitionTableOffset] == 0xAA
            && image[PartitionTableOffset + 1] == 0x50)
        {
            // Byte 3 of the image header: the flash size in the high nibble (0 = 1 MB, 1 = 2 MB… 4 = 16 MB).
            var code = image[BootloaderOffset + 3] >> 4;
            var declared = code <= 4 ? (1L << 20) << code : 0;
            return new FirmwareImageInfo(FirmwareImageKind.FullFlash, image.Length, declared);
        }

        return new FirmwareImageInfo(
            image.Length > 0 && image[0] == ImageMagic ? FirmwareImageKind.SingleImage : FirmwareImageKind.Unrecognized,
            image.Length,
            0);
    }

    /// <summary>Why the image must not be written from address 0, or null when it may.</summary>
    public string? RefusalReason() => Kind switch
    {
        FirmwareImageKind.SingleImage => FlasherText.T(
            "Файл начинается с заголовка образа ESP32: это отдельная часть прошивки (загрузчик или приложение), которую записывают по своему смещению, а не с адреса 0x0. Нужен полный образ флеш-памяти: файл прошивки MKS DLC32 или считанная с платы копия.",
            "The file starts with the header of an ESP32 image: it is a part of a firmware (a bootloader or an application) that goes to an offset of its own, not to address 0x0. A whole flash image is needed: a firmware file of the MKS DLC32 or a copy read from a board."),
        FirmwareImageKind.Unrecognized => FlasherText.T(
            "Файл не похож на полный образ флеш-памяти: в нём нет загрузчика ESP32 по смещению 0x1000 и таблицы разделов по смещению 0x8000.",
            "The file does not look like a whole flash image: it has no ESP32 bootloader at offset 0x1000 and no partition table at offset 0x8000."),
        _ => null,
    };
}
