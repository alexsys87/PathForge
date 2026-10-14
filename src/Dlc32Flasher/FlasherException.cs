namespace Dlc32Flasher;

/// <summary>Why an operation of the flasher failed.</summary>
public enum FlasherError
{
    /// <summary>The COM port is busy (another program holds it) or does not exist.</summary>
    PortUnavailable,

    /// <summary>The ROM bootloader never answered: no auto-reset, no power, the wrong port.</summary>
    NoAnswer,

    /// <summary>The chip answered, but it started its firmware instead of the bootloader (GPIO0 was not low at reset).</summary>
    WrongBootMode,

    /// <summary>Another chip than the ESP32 answered.</summary>
    WrongChip,

    /// <summary>The chip stopped answering in the middle of the operation (cable, power, a speed that is too high).</summary>
    Timeout,

    /// <summary>The bootloader refused a command (the error code is in the message).</summary>
    CommandFailed,

    /// <summary>The image does not fit into the flash.</summary>
    DoesNotFit,

    /// <summary>The image is not a whole flash image of an ESP32 board.</summary>
    BadImage,

    /// <summary>What was written is not what the image holds (MD5 differs).</summary>
    VerifyFailed,
}

/// <summary>An operation of the flasher failed; the message is ready to be shown to the operator.</summary>
public sealed class FlasherException : Exception
{
    public FlasherException(FlasherError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    public FlasherError Error { get; }

    /// <summary>The flash may have been changed before the failure: the board may not start until it is written again.</summary>
    public bool FlashChanged { get; init; }
}
