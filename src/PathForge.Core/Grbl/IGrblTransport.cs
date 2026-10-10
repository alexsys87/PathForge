namespace PathForge.Core.Grbl;

/// <summary>Connection to a GRBL controller (a serial port or a network socket). Implementations must be thread-safe for writing.</summary>
public interface IGrblTransport : IDisposable
{
    /// <summary>A complete line received from the controller (without the line break). May be raised on any thread.</summary>
    event Action<string>? LineReceived;

    /// <summary>The connection was lost (cable pulled, port error, network down). May be raised on any thread.</summary>
    event Action<Exception>? Failed;

    /// <summary>
    /// A message from the transport itself that the operator should see, e.g. that the board was found in its
    /// bootloader and is being restarted. May be raised on any thread. Transports with nothing to say need not implement it.
    /// </summary>
    event Action<string>? Notice
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Called once the event handlers are attached. A transport that reads on its own thread starts here,
    /// so that nothing the controller sends right after connecting (the startup banner) is lost.
    /// </summary>
    void Start()
    {
    }

    /// <summary>Sends a command line; the line break is added by the caller.</summary>
    void Write(string text);

    /// <summary>Sends a single-byte real-time command (status, hold, resume, reset, overrides).</summary>
    void WriteRealtime(byte command);
}
