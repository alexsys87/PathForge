namespace PathForge.Core.Grbl;

/// <summary>
/// The ROM serial bootloader of ESP32 boards (MKS DLC32) as far as it matters for GRBL: telling whether a board that
/// does not answer is sitting in it. The chip starts the bootloader instead of the firmware when GPIO0 is held low at
/// power-on, and the USB-serial chip pulls GPIO0 low through its DTR line. The bootloader does not speak GRBL: it prints
/// "waiting for download" once and then answers only the esptool protocol (SLIP frames).
/// </summary>
public static class Esp32Bootloader
{
    private const byte SlipEnd = 0xC0;

    /// <summary>
    /// The esptool SYNC command in a SLIP frame: direction 0 (request), command 8, size 36, no checksum, and the data
    /// 07 07 12 20 followed by 32 × 55. The ROM bootloader answers it at any baud rate; a running GRBL ignores it.
    /// </summary>
    public static byte[] SyncFrame()
    {
        var frame = new byte[1 + 8 + 36 + 1];
        frame[0] = SlipEnd;
        frame[2] = 0x08; // the command SYNC; frame[1] = 0 is the direction "request"
        frame[3] = 36; // the data size, low byte; the high byte and the four checksum bytes stay 0
        frame[9] = 0x07;
        frame[10] = 0x07;
        frame[11] = 0x12;
        frame[12] = 0x20;
        Array.Fill(frame, (byte)0x55, 13, 32);
        frame[^1] = SlipEnd;
        return frame;
    }

    /// <summary>The data holds an answer to SYNC: a SLIP frame with the direction 1 (response) and the command 8.</summary>
    public static bool IsSyncResponse(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] == SlipEnd && data[i + 1] == 0x01 && data[i + 2] == 0x08)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text is what the ROM bootloader prints when it starts and waits for a firmware download.</summary>
    public static bool IsDownloadBanner(string text) =>
        text.Contains("waiting for download", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("DOWNLOAD_BOOT", StringComparison.Ordinal) ||
        text.Contains("DOWNLOAD(", StringComparison.Ordinal);

    /// <summary>
    /// The data shows a running GRBL firmware: a status report ("&lt;Idle|MPos:…"), the startup banner or a message.
    /// The ROM bootloader and the chip's boot log never print any of these.
    /// </summary>
    public static bool IsGrblTraffic(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == (byte)'<' && char.IsAsciiLetter((char)data[i + 1]))
            {
                return true;
            }
        }

        return data.IndexOf("Grbl"u8) >= 0 || data.IndexOf("[MSG:"u8) >= 0;
    }
}
