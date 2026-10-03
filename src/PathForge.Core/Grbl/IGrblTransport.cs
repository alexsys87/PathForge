namespace PathForge.Core.Grbl;

/// <summary>Connection to a GRBL controller (normally a serial port). Implementations must be thread-safe for writing.</summary>
public interface IGrblTransport : IDisposable
{
    /// <summary>A complete line received from the controller (without the line break). May be raised on any thread.</summary>
    event Action<string>? LineReceived;

    /// <summary>The connection was lost (cable pulled, port error). May be raised on any thread.</summary>
    event Action<Exception>? Failed;

    /// <summary>Sends a command line; the line break is added by the caller.</summary>
    void Write(string text);

    /// <summary>Sends a single-byte real-time command (status, hold, resume, reset, overrides).</summary>
    void WriteRealtime(byte command);
}
