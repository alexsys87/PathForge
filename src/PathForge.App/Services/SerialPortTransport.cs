using System.IO;
using System.IO.Ports;
using System.Text;
using PathForge.Core.Grbl;
using PathForge.Core.Localization;

namespace PathForge.App.Services;

/// <summary>
/// GRBL connection over a (USB) serial port. DTR and RTS are kept low and there is no hardware flow control, so opening
/// the port does not reset the board (use the soft reset if a restart is needed).
/// </summary>
public sealed class SerialPortTransport : IGrblTransport
{
    private readonly SerialPort _port;
    private readonly object _writeLock = new();
    private readonly StringBuilder _incoming = new();
    private bool _failed;

    public SerialPortTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate)
        {
            Encoding = Encoding.ASCII,
            NewLine = "\n",

            // No hardware flow control and no modem-control lines. On the MKS DLC32 (ESP32 behind a CH340) DTR/RTS are
            // wired to the auto-reset circuit (EN/GPIO0): asserting them on open resets the board into its bootloader.
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            WriteTimeout = 2000,
            ReadTimeout = 500,
        };
        _port.DataReceived += OnDataReceived;
        _port.Open();
    }

    public event Action<string>? LineReceived;

    public event Action<Exception>? Failed;

    public static string[] PortNames() => SerialPort.GetPortNames().Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();

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

    private void EnsureOpen()
    {
        if (!_port.IsOpen)
        {
            throw new IOException(Loc.T("Порт закрыт.", "The port is closed."));
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        string text;
        try
        {
            text = _port.ReadExisting();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Fail(ex);
            return;
        }

        var lines = new List<string>();
        lock (_incoming)
        {
            foreach (var c in text)
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
