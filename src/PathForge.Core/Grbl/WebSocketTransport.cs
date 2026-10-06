using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>How commands reach the board over a WebSocket connection.</summary>
public enum WebSocketCommandRoute
{
    /// <summary>
    /// Grbl_Esp32 web interface (MKS DLC32, ESP3D WebUI): GRBL output arrives on the WebSocket, while the
    /// board ignores what is written to it; commands go to <c>http://host/command?commandText=…</c>.
    /// </summary>
    Http,

    /// <summary>Serial-to-WebSocket bridges (ESP8266 for LaserGRBL) and FluidNC: commands are WebSocket frames.</summary>
    WebSocket,
}

/// <summary>
/// GRBL connection over a WebSocket, as used by the web interface of Grbl_Esp32 boards (MKS DLC32: port 81,
/// the HTTP port + 1) and by WiFi bridges. Writes never block the caller: they are queued and sent in order
/// by a background thread, so the UI stays responsive while the network is slow. Real-time commands keep
/// their place in the queue (a jog cancel must not overtake the jogs sent before it), but status queries are
/// not piled up when the board answers slower than they are polled.
/// </summary>
public sealed class WebSocketTransport : IGrblTransport
{
    public const int DefaultPort = 81;

    /// <summary>Longest command batch sent in one HTTP request (several queued lines go together).</summary>
    private const int MaxBatchLength = 200;

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Text messages of the Grbl_Esp32 web interface protocol, not GRBL output.</summary>
    private static readonly string[] ServiceMessages = { "CURRENT_ID:", "ACTIVE_ID:", "PING:", "DHT:" };

    private readonly ClientWebSocket _socket;
    private readonly HttpClient? _http;
    private readonly WebSocketCommandRoute _route;
    private readonly Queue<string> _outgoing = new();
    private readonly StringBuilder _incoming = new();
    private Thread? _reader;
    private Thread? _sender;
    private bool _statusQueued;
    private volatile bool _disposed;
    private int _failed;

    /// <summary>Connects to <c>ws://host:port/</c>; throws <see cref="IOException"/> or <see cref="TimeoutException"/> when the board does not answer.</summary>
    /// <param name="httpPort">Port of the web interface for <see cref="WebSocketCommandRoute.Http"/>; by default <paramref name="port"/> − 1.</param>
    public WebSocketTransport(string host, int port = DefaultPort, WebSocketCommandRoute route = WebSocketCommandRoute.Http,
        int? httpPort = null, TimeSpan? connectTimeout = null)
    {
        _route = route;
        _socket = new ClientWebSocket();
        // The board is on the local network: a system proxy would only get in the way.
        _socket.Options.Proxy = null;
        // The protocol name the Grbl_Esp32 web interface asks for; servers that do not know it ignore it.
        _socket.Options.AddSubProtocol("arduino");
        using (var timeout = new CancellationTokenSource(connectTimeout ?? DefaultConnectTimeout))
        {
            try
            {
                _socket.ConnectAsync(new Uri($"ws://{host}:{port}/"), timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                _socket.Dispose();
                throw new TimeoutException(Loc.T($"{host}:{port} не отвечает.", $"{host}:{port} does not answer."));
            }
            catch (WebSocketException ex)
            {
                _socket.Dispose();
                throw new IOException(ex.InnerException?.Message ?? ex.Message, ex);
            }
            catch
            {
                _socket.Dispose();
                throw;
            }
        }

        if (route == WebSocketCommandRoute.Http)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                MaxConnectionsPerServer = 1,
                ConnectTimeout = connectTimeout ?? DefaultConnectTimeout,
            };
            _http = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://{host}:{httpPort ?? port - 1}/"),
                Timeout = TimeSpan.FromSeconds(5),
            };
        }
    }

    public event Action<string>? LineReceived;

    public event Action<Exception>? Failed;

    public void Start()
    {
        if (_reader is not null || _disposed)
        {
            return;
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "GRBL WebSocket reader" };
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "GRBL WebSocket sender" };
        _reader.Start();
        _sender.Start();
    }

    public void Write(string text) => Enqueue(text);

    public void WriteRealtime(byte command)
    {
        lock (_outgoing)
        {
            if (command == (byte)'?')
            {
                if (_statusQueued)
                {
                    return;
                }

                _statusQueued = true;
            }
        }

        Enqueue(((char)command).ToString());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_outgoing)
        {
            _disposed = true;
            Monitor.PulseAll(_outgoing);
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Already broken: nothing to close politely.
        }

        _socket.Abort();
        _socket.Dispose();
        _http?.Dispose();
    }

    private void Enqueue(string text)
    {
        lock (_outgoing)
        {
            if (_disposed || _failed != 0)
            {
                throw new IOException(Loc.T("Соединение закрыто.", "The connection is closed."));
            }

            _outgoing.Enqueue(text);
            Monitor.Pulse(_outgoing);
        }
    }

    private void SendLoop()
    {
        try
        {
            while (TakeBatch() is { } batch)
            {
                if (_route == WebSocketCommandRoute.Http)
                {
                    SendOverHttp(batch);
                }
                else
                {
                    SendOverWebSocket(batch);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            Fail(ex is IOException ? ex : new IOException(ex.Message, ex));
        }
    }

    /// <summary>Waits for queued writes and takes as many as fit into one request, in order.</summary>
    private List<string>? TakeBatch()
    {
        lock (_outgoing)
        {
            while (_outgoing.Count == 0 && !_disposed && _failed == 0)
            {
                Monitor.Wait(_outgoing);
            }

            if (_disposed || _failed != 0)
            {
                return null;
            }

            var batch = new List<string>();
            var length = 0;
            while (_outgoing.Count > 0 && (batch.Count == 0 || length + _outgoing.Peek().Length <= MaxBatchLength))
            {
                var item = _outgoing.Dequeue();
                if (item == "?")
                {
                    _statusQueued = false;
                }

                batch.Add(item);
                length += item.Length;
            }

            return batch;
        }
    }

    private void SendOverHttp(List<string> batch)
    {
        // The board splits the text at line breaks and adds the line break back to every command, except to a
        // single real-time character. Characters 0x80–0xFF (jog cancel, overrides) travel as UTF-8 (C2 xx),
        // which the board turns back into the single byte.
        var text = string.Join("\n", batch.Select(item => item.TrimEnd('\n')));
        using var response = _http!.Send(new HttpRequestMessage(HttpMethod.Get, "command?commandText=" + Uri.EscapeDataString(text)));
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new IOException(Loc.T(
                "Плата требует вход в веб-интерфейс (включена аутентификация) — подключитесь по USB или telnet.",
                "The board requires a web interface login (authentication is on) — connect over USB or telnet."));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IOException(Loc.T($"Плата ответила HTTP {(int)response.StatusCode}.", $"The board answered HTTP {(int)response.StatusCode}."));
        }

        using var reader = new StreamReader(response.Content.ReadAsStream());
        if (reader.ReadToEnd().Trim() == "Error")
        {
            // The command did not fit into the board's input buffer: the count of bytes in flight is wrong now.
            throw new IOException(Loc.T("Буфер команд платы переполнен.", "The board command buffer overflowed."));
        }
    }

    private void SendOverWebSocket(List<string> batch)
    {
        var text = string.Concat(batch);
        var bytes = text.Select(c => (byte)c).ToArray();
        // Bridges pass the bytes to the serial port as they are; text frames must be valid UTF-8, so a frame
        // with a real-time byte above 0x7F goes as binary.
        var type = bytes.Any(b => b >= 0x80) ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _socket.SendAsync(bytes, type, true, timeout.Token).GetAwaiter().GetResult();
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        var message = new List<byte>();
        try
        {
            while (!_disposed)
            {
                var result = _socket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Fail(new IOException(Loc.T("Плата закрыла соединение.", "The board closed the connection.")));
                    return;
                }

                message.AddRange(buffer.AsSpan(0, result.Count).ToArray());
                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary || !IsServiceMessage(message))
                {
                    Receive(message);
                }

                message.Clear();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            Fail(ex is IOException ? ex : new IOException(ex.Message, ex));
        }
    }

    private static bool IsServiceMessage(List<byte> message)
    {
        var text = Encoding.ASCII.GetString(message.ToArray(), 0, Math.Min(message.Count, 16));
        return ServiceMessages.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
    }

    private void Receive(List<byte> data)
    {
        // Only the reader thread touches the line buffer.
        var lines = new List<string>();
        foreach (var b in data)
        {
            if (b == '\n')
            {
                lines.Add(_incoming.ToString().TrimEnd('\r'));
                _incoming.Clear();
            }
            else if (b < 0x80)
            {
                _incoming.Append((char)b);
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

        lock (_outgoing)
        {
            Monitor.PulseAll(_outgoing);
        }

        Failed?.Invoke(error);
    }
}
