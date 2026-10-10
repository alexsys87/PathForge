using System.IO;
using System.IO.Ports;
using System.Text;
using PathForge.Core.Grbl;
using PathForge.Core.Localization;

namespace PathForge.App.Services;

/// <summary>
/// GRBL connection over a (USB) serial port. Opening the port raises DTR, which resets most Arduino-based boards (the
/// usual way GRBL starts). For an ESP32 board such as the MKS DLC32 the constructor keeps DTR and RTS low instead, because
/// they are wired to the boot pins of the ESP32, and a board that waits in the ESP32 ROM bootloader (the driver held DTR
/// when it was powered on) is recognised and restarted (<see cref="Esp32BootloaderRecovery"/>).
/// </summary>
public sealed class SerialPortTransport : IGrblTransport, ISerialLink
{
    private readonly SerialPort _port;
    private readonly object _writeLock = new();
    private readonly StringBuilder _incoming = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Esp32BootloaderRecovery? _recovery;
    private bool _failed;

    /// <param name="portName">COM port of the board.</param>
    /// <param name="baudRate">Speed of the port (115200 for GRBL and the MKS DLC32).</param>
    /// <param name="esp32Board">
    /// The board is an ESP32 (MKS DLC32): DTR and RTS stay low, so opening the port does not push it into its bootloader,
    /// and a board found waiting in the bootloader is restarted. False (the default) is the plain GRBL behaviour:
    /// DTR is raised, which resets Arduino-based boards, and nothing else is done.
    /// </param>
    public SerialPortTransport(string portName, int baudRate, bool esp32Board = false)
    {
        _port = new SerialPort(portName, baudRate)
        {
            Encoding = Encoding.ASCII,
            NewLine = "\n",

            // No hardware flow control. DTR: raised on open for Arduino boards, which restart on it. On an ESP32 board
            // (MKS DLC32 behind a CH340) DTR/RTS are wired to the auto-reset circuit (EN/GPIO0), so asserting them
            // resets the board into its bootloader; both stay low there.
            Handshake = Handshake.None,
            DtrEnable = !esp32Board,
            RtsEnable = false,
            WriteTimeout = 2000,
            ReadTimeout = 500,
        };
        if (esp32Board)
        {
            _recovery = new Esp32BootloaderRecovery(this, text => Notice?.Invoke(text));
        }

        _port.DataReceived += OnDataReceived;
        _port.Open();
    }

    public event Action<string>? LineReceived;

    public event Action<Exception>? Failed;

    public event Action<string>? Notice;

    public static string[] PortNames() => SerialPort.GetPortNames().Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// For an ESP32 board: starts watching for a board in the bootloader. Runs off the calling (UI) thread, because the
    /// recovery blocks on port writes. Does nothing for other boards.
    /// </summary>
    public void Start()
    {
        if (_recovery is { } recovery)
        {
            _ = Task.Run(() => WatchBootloaderAsync(recovery));
        }
    }

    public void Write(string text)
    {
        lock (_writeLock)
        {
            EnsureOpen();
            _port.Write(text);
        }
    }

    public void WriteRealtime(byte command)
    {
        lock (_writeLock)
        {
            EnsureOpen();
            _port.Write(new[] { command }, 0, 1);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _port.DataReceived -= OnDataReceived;
        try
        {
            lock (_writeLock)
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                }
            }

            _port.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The device is already gone (USB unplugged): closing its handle fails, there is nothing left to release.
        }
    }

    void ISerialLink.WriteRaw(byte[] data)
    {
        lock (_writeLock)
        {
            EnsureOpen();
            _port.Write(data, 0, data.Length);
        }
    }

    void ISerialLink.SetModemLines(bool dtr, bool rts)
    {
        lock (_writeLock)
        {
            EnsureOpen();
            _port.DtrEnable = dtr;
            _port.RtsEnable = rts;
        }
    }

    void ISerialLink.DiscardReceivedText()
    {
        lock (_incoming)
        {
            _incoming.Clear();
        }
    }

    private async Task WatchBootloaderAsync(Esp32BootloaderRecovery recovery)
    {
        try
        {
            await recovery.RunAsync(_stop.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            // The port is gone (USB unplugged): the controller reports the lost connection by itself.
        }
    }

    private void EnsureOpen()
    {
        if (!_port.IsOpen)
        {
            throw new IOException(Loc.T("Порт закрыт.", "The port is closed."));
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        byte[] data;
        try
        {
            var count = _port.BytesToRead;
            data = new byte[count];
            var read = count > 0 ? _port.Read(data, 0, count) : 0;
            if (read != count)
            {
                Array.Resize(ref data, read);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Fail(ex);
            return;
        }

        if (data.Length == 0)
        {
            return;
        }

        _recovery?.OnReceived(data);

        var lines = new List<string>();
        lock (_incoming)
        {
            foreach (var c in Encoding.ASCII.GetString(data))
            {
                if (c == '\n')
                {
                    lines.Add(_incoming.ToString().TrimEnd('\r'));
                    _incoming.Clear();
                }
                else
                {
                    _incoming.Append(c);
                }
            }
        }

        foreach (var line in lines)
        {
            LineReceived?.Invoke(line);
        }
    }

    private void Fail(Exception error)
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        Failed?.Invoke(error);
    }
}
