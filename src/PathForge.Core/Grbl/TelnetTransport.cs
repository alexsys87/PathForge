using System.Net.Sockets;
using System.Text;
using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>
/// GRBL connection over the network: the plain TCP ("telnet") stream of Grbl_Esp32 boards such as the
/// MKS DLC32 (port 23 by default), FluidNC or a serial-to-WiFi bridge. GRBL speaks the same protocol as
/// over USB, real-time commands included; telnet negotiation sequences that some bridges send are skipped.
/// </summary>
public sealed class TelnetTransport : IGrblTransport
{
    public const int DefaultPort = 23;

    private const byte Iac = 255;
    private const byte SubnegotiationBegin = 250;
    private const byte SubnegotiationEnd = 240;
    private const byte Will = 251;
    private const byte Dont = 254;

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(4);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    private readonly StringBuilder _incoming = new();
    private Thread? _reader;
    private TelnetState _telnetState;
    private volatile bool _disposed;
    private int _failed;

    /// <summary>Connects to the board; throws <see cref="SocketException"/> or <see cref="TimeoutException"/> when it does not answer.</summary>
    public TelnetTransport(string host, int port = DefaultPort, TimeSpan? connectTimeout = null)
    {
        _client = new TcpClient { NoDelay = true };
        using var timeout = new CancellationTokenSource(connectTimeout ?? DefaultConnectTimeout);
        try
        {
            _client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _client.Dispose();
            throw new TimeoutException(Loc.T($"{host}:{port} не отвечает.", $"{host}:{port} does not answer."));
        }
        catch
        {
            _client.Dispose();
            throw;
        }

        _stream = _client.GetStream();
        _stream.WriteTimeout = 2000;
    }

    public event Action<string>? LineReceived;

    public event Action<Exception>? Failed;

    public void Start()
    {
        if (_reader is not null || _disposed)
        {
            return;
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "GRBL telnet reader" };
        _reader.Start();
    }

    public void Write(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        lock (_writeLock)
        {
            _stream.Write(bytes, 0, bytes.Length);
        }
    }

    public void WriteRealtime(byte command)
    {
        lock (_writeLock)
        {
            _stream.WriteByte(command);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // A graceful close tells the board that the client is gone: Grbl_Esp32 accepts a single telnet
        // client and refuses new connections while it believes the old one is still there.
        try
        {
            _client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already broken.
        }

        _client.Dispose();
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_disposed)
            {
                var count = _stream.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    Fail(new IOException(Loc.T("Плата закрыла соединение.", "The board closed the connection.")));
                    return;
                }

                Receive(buffer.AsSpan(0, count));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Fail(ex);
        }
    }

    private void Receive(ReadOnlySpan<byte> data)
    {
        // Only this reader thread touches the line buffer.
        var lines = new List<string>();
        foreach (var b in data)
        {
            switch (_telnetState)
            {
                case TelnetState.Data when b == Iac:
                    _telnetState = TelnetState.Command;
                    break;
                case TelnetState.Data when b == '\n':
                    lines.Add(_incoming.ToString().TrimEnd('\r'));
                    _incoming.Clear();
                    break;
                case TelnetState.Data:
                    if (b < 0x80)
                    {
                        _incoming.Append((char)b);
                    }

                    break;
                case TelnetState.Command:
                    _telnetState = b switch
                    {
                        >= Will and <= Dont => TelnetState.Option,
                        SubnegotiationBegin => TelnetState.Subnegotiation,
                        _ => TelnetState.Data,
                    };
                    break;
                case TelnetState.Option:
                    _telnetState = TelnetState.Data;
                    break;
                case TelnetState.Subnegotiation:
                    if (b == Iac)
                    {
                        _telnetState = TelnetState.SubnegotiationIac;
                    }

                    break;
                case TelnetState.SubnegotiationIac:
                    _telnetState = b == SubnegotiationEnd ? TelnetState.Data : TelnetState.Subnegotiation;
                    break;
            }
        }

        foreach (var line in lines)
        {
            LineReceived?.Invoke(line);
        }
    }

    private void Fail(Exception error)
    {
        if (_disposed || Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        Failed?.Invoke(error);
    }

    private enum TelnetState
    {
        Data,
        Command,
        Option,
        Subnegotiation,
        SubnegotiationIac,
    }
}
