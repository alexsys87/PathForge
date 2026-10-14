using System.IO.Ports;

namespace Dlc32Flasher;

/// <summary>The serial port as the flasher needs it: bytes in and out, the speed and the lines DTR and RTS.</summary>
public interface IFlasherPort : IDisposable
{
    /// <summary>Speed of the port in baud.</summary>
    int BaudRate { get; set; }

    /// <summary>Sets the modem lines. On the MKS DLC32 (and most ESP32 boards) they drive EN and GPIO0 through two transistors.</summary>
    void SetLines(bool dtr, bool rts);

    void Write(ReadOnlySpan<byte> data);

    /// <summary>Reads what has arrived, waiting up to <paramref name="timeout"/> for the first byte; 0 when nothing came.</summary>
    int Read(Span<byte> buffer, TimeSpan timeout);

    /// <summary>Drops everything received and not read yet.</summary>
    void DiscardInput();
}

/// <summary>A COM port of the computer (System.IO.Ports).</summary>
public sealed class SerialFlasherPort : IFlasherPort
{
    private readonly SerialPort _port;
    private byte[] _buffer = new byte[4096];
    private byte[] _readBuffer = new byte[4096];

    /// <exception cref="FlasherException">The port is busy or does not exist.</exception>
    public SerialFlasherPort(string portName, int baudRate = EspRomLoader.InitialBaud)
    {
        _port = new SerialPort(portName, baudRate)
        {
            Handshake = Handshake.None,
            // Both lines low: opening the port must not reset the board (the reset into the bootloader is done on purpose).
            DtrEnable = false,
            RtsEnable = false,
            ReadBufferSize = 64 * 1024,
            WriteTimeout = 10_000,
        };

        try
        {
            _port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            _port.Dispose();
            throw new FlasherException(
                FlasherError.PortUnavailable,
                FlasherText.T(
                    $"Не удалось открыть {portName}: порт занят другой программой или не существует. Закройте программы, которые держат порт (Flash Download Tool, терминал, LightBurn, программу управления станком), и проверьте кабель.",
                    $"Cannot open {portName}: the port is held by another program or does not exist. Close the programs that hold the port (Flash Download Tool, a terminal, LightBurn, the machine control program) and check the cable."),
                ex);
        }
    }

    /// <summary>The COM ports of the computer, sorted.</summary>
    public static string[] PortNames() =>
        SerialPort.GetPortNames().Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public int BaudRate
    {
        get => _port.BaudRate;
        set => _port.BaudRate = value;
    }

    public void SetLines(bool dtr, bool rts)
    {
        _port.DtrEnable = dtr;
        _port.RtsEnable = rts;
        // The usbser.sys driver sends the line state only when DTR changes: setting DTR again sends the new RTS too.
        _port.DtrEnable = dtr;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_buffer.Length < data.Length)
        {
            _buffer = new byte[data.Length];
        }

        data.CopyTo(_buffer);
        _port.Write(_buffer, 0, data.Length);
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        _port.ReadTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
        if (_readBuffer.Length < buffer.Length)
        {
            _readBuffer = new byte[buffer.Length];
        }

        try
        {
            var count = _port.Read(_readBuffer, 0, buffer.Length);
            _readBuffer.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void DiscardInput() => _port.DiscardInBuffer();

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen)
            {
                _port.Close();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The device is gone already (USB unplugged): nothing is left to release.
        }

        _port.Dispose();
    }
}
